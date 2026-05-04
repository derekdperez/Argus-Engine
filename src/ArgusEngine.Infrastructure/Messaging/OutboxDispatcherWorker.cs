using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Buffers;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Observability;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class OutboxDispatcherWorker(
    IDbContextFactory<ArgusDbContext> dbFactory,
    IPublishEndpoint publish,
    ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    private const int MaxAttemptsBeforeDeadLetter = 10;
    private static readonly SearchValues<char> TypeSeparatorSearchValues = SearchValues.Create(['.', '/']);

    private static readonly Action<ILogger, string, Exception?> LogOutboxDispatcherStarting =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, nameof(LogOutboxDispatcherStarting)),
            "Outbox dispatcher {WorkerId} starting.");

    private static readonly Action<ILogger, Exception?> LogOutboxDispatcherLoopFault =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(LogOutboxDispatcherLoopFault)),
            "Outbox dispatcher loop fault.");

    private static readonly Action<ILogger, Guid, int, string, Exception?> LogOutboxMessageDeadLettered =
        LoggerMessage.Define<Guid, int, string>(
            LogLevel.Error,
            new EventId(3, nameof(LogOutboxMessageDeadLettered)),
            "Outbox message {OutboxId} moved to dead-letter after {Attempts} attempts. Error: {Error}");

    private static readonly Action<ILogger, Guid, string, string, Exception?> LogOutboxMessageStaleLease =
        LoggerMessage.Define<Guid, string, string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogOutboxMessageStaleLease)),
            "Outbox message {OutboxId} was not marked {TargetState} because worker {WorkerId} no longer owns the active lease.");

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogOutboxDispatcherStarting(logger, _workerId, null);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var leased = await TryLeaseNextAsync(stoppingToken).ConfigureAwait(false);

                if (leased is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await DispatchAsync(leased, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogOutboxDispatcherLoopFault(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<OutboxMessage?> TryLeaseNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var lockUntil = now.AddMinutes(2);

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH candidate AS (
                SELECT o.*
                FROM outbox_messages o
                WHERE (
                    (o.state IN ('Pending', 'Failed') AND o.next_attempt_at_utc <= @now)
                    OR (o.state = 'InFlight' AND o.locked_until_utc < @now)
                )
                AND o.state <> 'DeadLetter'
                ORDER BY o.next_attempt_at_utc ASC, o.created_at_utc ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE outbox_messages o
            SET state = 'InFlight',
                locked_by = @worker_id,
                locked_until_utc = @lock_until,
                updated_at_utc = @now,
                attempt_count = o.attempt_count + 1
            FROM candidate
            WHERE o.id = candidate.id
            RETURNING o.*;
            """;

        AddParameter(cmd, "now", now);
        AddParameter(cmd, "lock_until", lockUntil);
        AddParameter(cmd, "worker_id", _workerId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return MapOutboxMessage(reader);
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        using var activity = ArgusTracing.Source.StartActivity("outbox.dispatch", ActivityKind.Producer);
        var shortType = ShortMessageType(message.MessageType);

        activity?.SetTag("argus.outbox_id", message.Id);
        activity?.SetTag("argus.message_type", shortType);
        activity?.SetTag("argus.attempt_count", message.AttemptCount);
        activity?.SetTag("argus.correlation_id", message.CorrelationId);
        activity?.SetTag("argus.worker_id", _workerId);

        try
        {
            if (!TryDeserialize(message, out var payload, out var messageClrType))
            {
                const string error = "Unable to resolve or deserialize message key/payload.";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                activity?.SetTag("argus.outbox_state", OutboxMessageState.DeadLetter);

                await MarkDeadLetterAsync(message, error, ct).ConfigureAwait(false);
                return;
            }

            await publish.Publish(payload!, messageClrType!, ct).ConfigureAwait(false);
            var markedSucceeded = await MarkSucceededAsync(message, ct).ConfigureAwait(false);

            if (!markedSucceeded)
            {
                activity?.SetTag("argus.outbox_state", "StaleLease");
                return;
            }

            ArgusMeters.OutboxDispatched.Add(
                1,
                new KeyValuePair<string, object?>("message_type", shortType));

            activity?.SetTag("argus.outbox_state", OutboxMessageState.Succeeded);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);

            await MarkRetryOrDeadLetterAsync(message, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> MarkSucceededAsync(OutboxMessage message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var updated = await db.OutboxMessages
            .Where(x =>
                x.Id == message.Id &&
                x.LockedBy == _workerId &&
                x.State == OutboxMessageState.InFlight &&
                x.LockedUntilUtc >= now)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.State, OutboxMessageState.Succeeded)
                    .SetProperty(x => x.LockedBy, (string?)null)
                    .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAtUtc, now)
                    .SetProperty(x => x.DispatchedAtUtc, now)
                    .SetProperty(x => x.LastError, (string?)null),
                ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            LogOutboxMessageStaleLease(logger, message.Id, OutboxMessageState.Succeeded, _workerId, null);
            return false;
        }

        return true;
    }

    private async Task MarkRetryOrDeadLetterAsync(OutboxMessage message, string error, CancellationToken ct)
    {
        if (message.AttemptCount >= MaxAttemptsBeforeDeadLetter)
        {
            await MarkDeadLetterAsync(message, error, ct).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Max(0, message.AttemptCount)) * 2));

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.OutboxMessages
            .Where(x =>
                x.Id == message.Id &&
                x.LockedBy == _workerId &&
                x.State == OutboxMessageState.InFlight &&
                x.LockedUntilUtc >= now)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.State, OutboxMessageState.Failed)
                    .SetProperty(x => x.UpdatedAtUtc, now)
                    .SetProperty(x => x.NextAttemptAtUtc, now + delay)
                    .SetProperty(x => x.LockedBy, (string?)null)
                    .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, Truncate(error, 2048)),
                ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            LogOutboxMessageStaleLease(logger, message.Id, OutboxMessageState.Failed, _workerId, null);
        }
    }

    private async Task MarkDeadLetterAsync(OutboxMessage message, string error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var shortType = ShortMessageType(message.MessageType);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.OutboxMessages
            .Where(x =>
                x.Id == message.Id &&
                x.LockedBy == _workerId &&
                x.State == OutboxMessageState.InFlight &&
                x.LockedUntilUtc >= now)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.State, OutboxMessageState.DeadLetter)
                    .SetProperty(x => x.UpdatedAtUtc, now)
                    .SetProperty(x => x.LockedBy, (string?)null)
                    .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(x => x.LastError, Truncate(error, 2048)),
                ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            LogOutboxMessageStaleLease(logger, message.Id, OutboxMessageState.DeadLetter, _workerId, null);
            return;
        }

        ArgusMeters.OutboxDeadLettered.Add(
            1,
            new KeyValuePair<string, object?>("message_type", shortType));

        LogOutboxMessageDeadLettered(logger, message.Id, message.AttemptCount, error, null);
    }

    private static bool TryDeserialize(OutboxMessage message, out object? payload, out Type? messageType)
    {
        payload = null;
        messageType = null;

        if (!OutboxMessageTypeRegistry.TryResolve(message.MessageType, out messageType))
        {
            return false;
        }

        payload = JsonSerializer.Deserialize(message.PayloadJson, messageType);
        return payload is not null;
    }

    internal static string ShortMessageType(string messageType)
    {
        var comma = messageType.IndexOf(',');
        var typeName = comma >= 0 ? messageType[..comma] : messageType;
        var separator = typeName.LastIndexOfAny(TypeSeparatorSearchValues);

        return separator >= 0 ? typeName[(separator + 1)..] : typeName;
    }

    private static OutboxMessage MapOutboxMessage(DbDataReader reader) => new()
    {
        Id = reader.GetGuid(reader.GetOrdinal("id")),
        MessageType = reader.GetString(reader.GetOrdinal("message_type")),
        PayloadJson = reader.GetString(reader.GetOrdinal("payload_json")),
        EventId = reader.GetGuid(reader.GetOrdinal("event_id")),
        CorrelationId = reader.GetGuid(reader.GetOrdinal("correlation_id")),
        CausationId = reader.GetGuid(reader.GetOrdinal("causation_id")),
        OccurredAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("occurred_at_utc")),
        Producer = reader.GetString(reader.GetOrdinal("producer")),
        State = reader.GetString(reader.GetOrdinal("state")),
        AttemptCount = reader.GetInt32(reader.GetOrdinal("attempt_count")),
        CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at_utc")),
        UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at_utc")),
        NextAttemptAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("next_attempt_at_utc")),
        LastError = ReadNullableString(reader, "last_error"),
        LockedBy = ReadNullableString(reader, "locked_by"),
        LockedUntilUtc = ReadNullableDateTimeOffset(reader, "locked_until_utc"),
        DispatchedAtUtc = ReadNullableDateTimeOffset(reader, "dispatched_at_utc"),
    };

    private static string? ReadNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
