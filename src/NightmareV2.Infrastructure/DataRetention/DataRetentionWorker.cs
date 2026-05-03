using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NightmareV2.Application.DataRetention;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Observability;

namespace NightmareV2.Infrastructure.DataRetention;

public sealed class DataRetentionWorker(
    IDbContextFactory<NightmareDbContext> dbFactory,
    IOptions<DataRetentionOptions> options,
    DataRetentionRunState state,
    ILogger<DataRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.Value;

            if (opt.Enabled)
            {
                try
                {
                    var result = await RunOnceAsync(opt, stoppingToken).ConfigureAwait(false);
                    state.Record(result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Data retention run failed.");
                }
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task<DataRetentionRunResult> RunOnceAsync(DataRetentionOptions opt, CancellationToken ct)
    {
        var result = new DataRetentionRunResult
        {
            SucceededOutboxDeleted = await DeleteBatchesAsync(
                "outbox_messages",
                "id",
                "state = 'Succeeded' AND updated_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.SucceededOutboxRetentionDays),
                opt,
                "outbox_messages",
                ct).ConfigureAwait(false),

            FailedOutboxDeleted = await DeleteBatchesAsync(
                "outbox_messages",
                "id",
                "state = 'Failed' AND updated_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.FailedOutboxRetentionDays),
                opt,
                "outbox_messages",
                ct).ConfigureAwait(false),

            DeadLetterOutboxDeleted = await DeleteBatchesAsync(
                "outbox_messages",
                "id",
                "state = 'DeadLetter' AND updated_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.DeadLetterOutboxRetentionDays),
                opt,
                "outbox_messages",
                ct).ConfigureAwait(false),

            InboxDeleted = await DeleteBatchesAsync(
                "inbox_messages",
                "id",
                "updated_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.InboxRetentionDays),
                opt,
                "inbox_messages",
                ct).ConfigureAwait(false),

            BusJournalDeleted = await DeleteBatchesAsync(
                "bus_journal",
                "id",
                "occurred_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.BusJournalRetentionDays),
                opt,
                "bus_journal",
                ct).ConfigureAwait(false),

            HttpQueueDeleted = await DeleteBatchesAsync(
                "http_request_queue",
                "id",
                "state IN ('Succeeded', 'Failed') AND completed_at_utc IS NOT NULL AND completed_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.CompletedHttpQueueRetentionDays),
                opt,
                "http_request_queue",
                ct).ConfigureAwait(false),

            CloudUsageDeleted = await DeleteBatchesAsync(
                "cloud_resource_usage_samples",
                "id",
                "sampled_at_utc < {0}",
                DateTimeOffset.UtcNow.AddDays(-opt.CloudUsageRetentionDays),
                opt,
                "cloud_resource_usage_samples",
                ct).ConfigureAwait(false),

            CompletedAtUtc = DateTimeOffset.UtcNow
        };

        return result;
    }

    private async Task<int> DeleteBatchesAsync(
        string tableName,
        string idColumn,
        string whereSql,
        DateTimeOffset cutoff,
        DataRetentionOptions opt,
        string metricTableName,
        CancellationToken ct)
    {
        var total = 0;

        for (var i = 0; i < opt.MaxBatchesPerRun; i++)
        {
            var deleted = await DeleteBatchAsync(tableName, idColumn, whereSql, cutoff, opt.BatchSize, ct)
                .ConfigureAwait(false);

            if (deleted == 0)
                break;

            total += deleted;

            ArgusMeters.DataRetentionDeletedRows.Add(
                deleted,
                new KeyValuePair<string, object?>("table", metricTableName));

            if (opt.DelayBetweenBatchesMs > 0)
                await Task.Delay(opt.DelayBetweenBatchesMs, ct).ConfigureAwait(false);
        }

        return total;
    }

    private async Task<int> DeleteBatchAsync(
        string tableName,
        string idColumn,
        string whereSql,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sql = $"""
            DELETE FROM {tableName}
            WHERE {idColumn} IN (
                SELECT {idColumn}
                FROM {tableName}
                WHERE {whereSql}
                ORDER BY {idColumn}
                LIMIT {batchSize}
            );
            """;

        return await db.Database.ExecuteSqlRawAsync(sql, new object[] { cutoff }, ct).ConfigureAwait(false);
    }
}
