using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ArgusEngine.Application.DataRetention;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.DataRetention;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

public static class DataRetentionAdminEndpoints
{
    public static IEndpointRouteBuilder MapDataRetentionAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/data-retention/status", GetStatusAsync);
        app.MapPost("/api/admin/data-retention/run-now", RunNowAsync);
        app.MapPost("/api/admin/partitions/ensure", EnsurePartitionsAsync);

        return app;
    }

    private static IResult GetStatusAsync(
        IConfiguration configuration,
        DataRetentionRunState state,
        IOptions<DataRetentionOptions> options,
        HttpRequest request)
    {
        if (!IsAuthorized(configuration, request))
            return Results.Unauthorized();

        return Results.Ok(new
        {
            enabled = options.Value.Enabled,
            options = new
            {
                options.Value.RunIntervalMinutes,
                options.Value.SucceededOutboxRetentionDays,
                options.Value.SucceededOutboxRetentionHours,
                options.Value.FailedOutboxRetentionDays,
                options.Value.FailedOutboxRetentionHours,
                options.Value.DeadLetterOutboxRetentionDays,
                options.Value.DeadLetterOutboxRetentionHours,
                options.Value.BusJournalRetentionDays,
                options.Value.BusJournalRetentionHours,
                options.Value.BatchSize,
                options.Value.MaxBatchesPerRun,
                options.Value.ArchiveEventTablesBeforeDelete
            },
            state.LastRunAtUtc,
            state.LastResult
        });
    }

    private static async Task<IResult> RunNowAsync(
        IConfiguration configuration,
        DataRetentionWorker worker,
        DataRetentionRunState state,
        IOptions<DataRetentionOptions> options,
        HttpRequest request,
        [FromBody] DataRetentionRunRequest? body,
        CancellationToken ct)
    {
        if (!IsAuthorized(configuration, request))
            return Results.Unauthorized();

        if (!string.Equals(body?.Confirmation, "RUN DATA RETENTION", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Confirmation phrase RUN DATA RETENTION is required." });

        var effectiveOptions = ApplyRunOverrides(options.Value, body);
        var result = await worker.RunOnceAsync(effectiveOptions, ct).ConfigureAwait(false);
        state.Record(result);

        return Results.Ok(new
        {
            result,
            effective = new
            {
                effectiveOptions.SucceededOutboxRetentionDays,
                effectiveOptions.SucceededOutboxRetentionHours,
                effectiveOptions.FailedOutboxRetentionDays,
                effectiveOptions.FailedOutboxRetentionHours,
                effectiveOptions.DeadLetterOutboxRetentionDays,
                effectiveOptions.DeadLetterOutboxRetentionHours,
                effectiveOptions.BusJournalRetentionDays,
                effectiveOptions.BusJournalRetentionHours,
                effectiveOptions.BatchSize,
                effectiveOptions.MaxBatchesPerRun,
                effectiveOptions.ArchiveEventTablesBeforeDelete
            }
        });
    }

    private static async Task<IResult> EnsurePartitionsAsync(
        IConfiguration configuration,
        IPartitionMaintenanceService partitions,
        HttpRequest request,
        CancellationToken ct)
    {
        if (!IsAuthorized(configuration, request))
            return Results.Unauthorized();

        await partitions.EnsurePartitionsAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { ensuredAtUtc = DateTimeOffset.UtcNow });
    }

    private static bool IsAuthorized(IConfiguration configuration, HttpRequest request)
    {
        var configuredKey = configuration.GetArgusValue("DataMaintenance:ApiKey");
        if (string.IsNullOrWhiteSpace(configuredKey))
            return true;

        var provided = request.Headers["X-Maintenance-Key"].FirstOrDefault()
                       ?? request.Headers["X-Argus-Maintenance-Key"].FirstOrDefault();

        return string.Equals(provided, configuredKey, StringComparison.Ordinal);
    }

    private static DataRetentionOptions ApplyRunOverrides(DataRetentionOptions source, DataRetentionRunRequest? request)
    {
        var target = new DataRetentionOptions
        {
            Enabled = source.Enabled,
            RunIntervalMinutes = source.RunIntervalMinutes,
            SucceededOutboxRetentionDays = source.SucceededOutboxRetentionDays,
            SucceededOutboxRetentionHours = source.SucceededOutboxRetentionHours,
            FailedOutboxRetentionDays = source.FailedOutboxRetentionDays,
            FailedOutboxRetentionHours = source.FailedOutboxRetentionHours,
            DeadLetterOutboxRetentionDays = source.DeadLetterOutboxRetentionDays,
            DeadLetterOutboxRetentionHours = source.DeadLetterOutboxRetentionHours,
            InboxRetentionDays = source.InboxRetentionDays,
            InboxRetentionHours = source.InboxRetentionHours,
            BusJournalRetentionDays = source.BusJournalRetentionDays,
            BusJournalRetentionHours = source.BusJournalRetentionHours,
            ArchiveEventTablesBeforeDelete = source.ArchiveEventTablesBeforeDelete,
            ArchivedEventRetentionDays = source.ArchivedEventRetentionDays,
            CompletedHttpQueueRetentionDays = source.CompletedHttpQueueRetentionDays,
            FailedHttpQueueRetentionDays = source.FailedHttpQueueRetentionDays,
            HttpQueueRetentionDays = source.HttpQueueRetentionDays,
            PurgeStaleHttpQueueItems = source.PurgeStaleHttpQueueItems,
            StaleQueuedHttpQueueRetentionHours = source.StaleQueuedHttpQueueRetentionHours,
            StaleRetryHttpQueueRetentionHours = source.StaleRetryHttpQueueRetentionHours,
            StaleInFlightHttpQueueRetentionHours = source.StaleInFlightHttpQueueRetentionHours,
            CloudUsageRetentionDays = source.CloudUsageRetentionDays,
            BatchSize = source.BatchSize,
            DelayBetweenBatchesMs = source.DelayBetweenBatchesMs,
            MaxBatchesPerRun = source.MaxBatchesPerRun
        };

        if (request is null)
            return target;

        if (request.BusJournalRetentionHours is > 0)
        {
            target.BusJournalRetentionHours = request.BusJournalRetentionHours.Value;
            target.BusJournalRetentionDays = 0;
        }

        if (request.SucceededOutboxRetentionHours is > 0)
        {
            target.SucceededOutboxRetentionHours = request.SucceededOutboxRetentionHours.Value;
            target.SucceededOutboxRetentionDays = 0;
        }

        if (request.FailedOutboxRetentionHours is > 0)
        {
            target.FailedOutboxRetentionHours = request.FailedOutboxRetentionHours.Value;
            target.FailedOutboxRetentionDays = 0;
        }

        if (request.DeadLetterOutboxRetentionHours is > 0)
        {
            target.DeadLetterOutboxRetentionHours = request.DeadLetterOutboxRetentionHours.Value;
            target.DeadLetterOutboxRetentionDays = 0;
        }

        if (request.BatchSize is > 0)
            target.BatchSize = Math.Clamp(request.BatchSize.Value, 100, 20_000);

        if (request.MaxBatchesPerRun is > 0)
            target.MaxBatchesPerRun = Math.Clamp(request.MaxBatchesPerRun.Value, 1, 5_000);

        if (request.ArchiveEventTablesBeforeDelete is not null)
            target.ArchiveEventTablesBeforeDelete = request.ArchiveEventTablesBeforeDelete.Value;

        return target;
    }

    public sealed record DataRetentionRunRequest(
        string? Confirmation,
        int? BusJournalRetentionHours = null,
        int? SucceededOutboxRetentionHours = null,
        int? FailedOutboxRetentionHours = null,
        int? DeadLetterOutboxRetentionHours = null,
        int? BatchSize = null,
        int? MaxBatchesPerRun = null,
        bool? ArchiveEventTablesBeforeDelete = null);
}
