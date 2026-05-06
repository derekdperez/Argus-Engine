using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Observability;

public sealed class ArgusDatabaseLoggerProvider : ILoggerProvider
{
    private const int MaxQueuedErrors = 10_000;
    private const int BatchSize = 100;

    private static readonly TimeSpan EmptyQueueDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly string _component;
    private readonly ConcurrentQueue<SystemError> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processorTask;
    private int _queuedCount;
    private int _disposed;

    public ArgusDatabaseLoggerProvider(IServiceProvider serviceProvider, string component)
    {
        _serviceProvider = serviceProvider;
        _component = string.IsNullOrWhiteSpace(component) ? "unknown" : component;
        _processorTask = Task.Run(ProcessQueueAsync);
    }

    public string Component => _component;

    public void EnqueueLog(SystemError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (Volatile.Read(ref _queuedCount) >= MaxQueuedErrors)
        {
            return;
        }

        Interlocked.Increment(ref _queuedCount);
        _queue.Enqueue(error);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ArgusDatabaseLogger(categoryName, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            _processorTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Logger shutdown must never block host shutdown.
        }

        _cts.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            List<SystemError> batch = [];

            try
            {
                batch = DrainBatch();

                if (batch.Count == 0)
                {
                    await Task.Delay(EmptyQueueDelay, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                await WriteBatchAsync(batch, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                Requeue(batch);

                try
                {
                    await Task.Delay(FailureDelay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private List<SystemError> DrainBatch()
    {
        var batch = new List<SystemError>(BatchSize);

        while (batch.Count < BatchSize && _queue.TryDequeue(out var error))
        {
            Interlocked.Decrement(ref _queuedCount);
            batch.Add(error);
        }

        return batch;
    }

    private void Requeue(IEnumerable<SystemError> batch)
    {
        foreach (var error in batch)
        {
            EnqueueLog(error);
        }
    }

    private async Task WriteBatchAsync(
        List<SystemError> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<ArgusDbContext>>();

        if (dbFactory is null)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.SystemErrors.AddRange(batch);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ArgusDatabaseLogger : ILogger
{
    private readonly string _name;
    private readonly ArgusDatabaseLoggerProvider _provider;

    public ArgusDatabaseLogger(string name, ArgusDatabaseLoggerProvider provider)
    {
        _name = name;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return default;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Error && !IsInternalPersistenceCategory(_name);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = FormatMessage(state, exception, formatter);

        var error = new SystemError
        {
            Component = _provider.Component,
            MachineName = Environment.MachineName,
            LogLevel = logLevel.ToString(),
            Message = message,
            Exception = exception?.ToString(),
            LoggerName = _name,
            Timestamp = DateTimeOffset.UtcNow,
            MetadataJson = CreateMetadataJson(eventId, state, exception)
        };

        _provider.EnqueueLog(error);
    }

    private static bool IsInternalPersistenceCategory(string categoryName)
    {
        return categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || categoryName.StartsWith("ArgusEngine.Infrastructure.Observability", StringComparison.Ordinal)
            || categoryName.StartsWith("Npgsql", StringComparison.Ordinal);
    }

    private static string FormatMessage<TState>(
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = string.Empty;

        try
        {
            message = formatter(state, exception);
        }
        catch
        {
            // Fall back below. Logging must never throw back into application code.
        }

        message = string.IsNullOrWhiteSpace(message)
            ? exception?.Message ?? state?.ToString() ?? "Error log entry did not include a message."
            : message.Trim();

        if (exception is null)
        {
            return message;
        }

        var exceptionHeadline = GetExceptionHeadline(exception);

        if (IsGenericErrorMessage(message))
        {
            return exceptionHeadline;
        }

        if (!string.IsNullOrWhiteSpace(exception.Message)
            && !message.Contains(exception.Message, StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} — {exceptionHeadline}";
        }

        return message;
    }

    private static bool IsGenericErrorMessage(string message)
    {
        var normalized = message.Trim().TrimEnd('.');

        return normalized.Equals("An error has occurred", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("An error has occured", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("An unhandled exception has occurred while executing the request", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Unhandled exception rendering component", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Circuit host terminated unexpectedly", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExceptionHeadline(Exception exception)
    {
        var typeName = exception.GetType().Name;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "No exception message was provided."
            : exception.Message.Trim();

        return $"{typeName}: {message}";
    }

    private static string? CreateMetadataJson<TState>(
        EventId eventId,
        TState state,
        Exception? exception)
    {
        try
        {
            var metadata = new Dictionary<string, string?>();

            if (eventId.Id != 0)
            {
                metadata["event_id"] = eventId.Id.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(eventId.Name))
            {
                metadata["event_name"] = eventId.Name;
            }

            var activity = Activity.Current;
            if (activity is not null)
            {
                metadata["trace_id"] = activity.TraceId.ToString();
                metadata["span_id"] = activity.SpanId.ToString();
            }

            if (exception is not null)
            {
                metadata["exception_type"] = exception.GetType().FullName;
                metadata["exception_message"] = exception.Message;
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    var key = value.Key == "{OriginalFormat}" ? "message_template" : value.Key;
                    metadata[key] = value.Value?.ToString();
                }
            }

            return metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata);
        }
        catch
        {
            return null;
        }
    }
}
