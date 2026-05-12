using ArgusEngine.Application.DataRetention;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Infrastructure.DataRetention;

public sealed class DataRetentionWorker(
    IDbContextFactory<ArgusDbContext> dbFactory,
    IOptions<DataRetentionOptions> options,
    DataRetentionRunState state,
    ILogger<DataRetentionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(ExecuteAsync)), "Data retention run failed.");

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
                    LogRunFailed(logger, ex);
                }
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task<DataRetentionRunResult> RunOnceAsync(DataRetentionOptions opt, CancellationToken ct)
    {
        await EnsureArchiveTablesAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        var completedHttpQueueRetentionDays = opt.CompletedHttpQueueRetentionDays > 0
            ? opt.CompletedHttpQueueRetentionDays
            : opt.HttpQueueRetentionDays;

        var failedHttpQueueRetentionDays = opt.FailedHttpQueueRetentionDays > 0
            ? opt.FailedHttpQueueRetentionDays
            : opt.HttpQueueRetentionDays;

        var completedHttpQueueDeleted = await ArchiveThenDeleteBatchesAsync(
            tableName: "http_request_queue",
            archiveTableName: "archived_http_request_queue",
            idColumn: "id",
            whereSql: "state = 'Succeeded' AND COALESCE(completed_at_utc, updated_at_utc, created_at_utc) < {0}",
            cutoff: now.AddDays(-completedHttpQueueRetentionDays),
            opt,
            metricTableName: "http_request_queue",
            ct).ConfigureAwait(false);

        var failedHttpQueueDeleted = await ArchiveThenDeleteBatchesAsync(
            tableName: "http_request_queue",
            archiveTableName: "archived_http_request_queue",
            idColumn: "id",
            whereSql: "state = 'Failed' AND COALESCE(completed_at_utc, updated_at_utc, created_at_utc) < {0}",
            cutoff: now.AddDays(-failedHttpQueueRetentionDays),
            opt,
            metricTableName: "http_request_queue",
            ct).ConfigureAwait(false);

        var staleQueuedHttpQueueDeleted = opt.PurgeStaleHttpQueueItems
            ? await ArchiveThenDeleteBatchesAsync(
                tableName: "http_request_queue",
                archiveTableName: "archived_http_request_queue",
                idColumn: "id",
                whereSql: "state = 'Queued' AND created_at_utc < {0} AND (locked_until_utc IS NULL OR locked_until_utc < now())",
                cutoff: now.AddHours(-Math.Max(1, opt.StaleQueuedHttpQueueRetentionHours)),
                opt,
                metricTableName: "http_request_queue",
                ct).ConfigureAwait(false)
            : 0;

        var staleRetryHttpQueueDeleted = opt.PurgeStaleHttpQueueItems
            ? await ArchiveThenDeleteBatchesAsync(
                tableName: "http_request_queue",
                archiveTableName: "archived_http_request_queue",
                idColumn: "id",
                whereSql: "state = 'Retry' AND COALESCE(next_attempt_at_utc, updated_at_utc, created_at_utc) < {0} AND (locked_until_utc IS NULL OR locked_until_utc < now())",
                cutoff: now.AddHours(-Math.Max(1, opt.StaleRetryHttpQueueRetentionHours)),
                opt,
                metricTableName: "http_request_queue",
                ct).ConfigureAwait(false)
            : 0;

        var staleInFlightHttpQueueDeleted = opt.PurgeStaleHttpQueueItems
            ? await ArchiveThenDeleteBatchesAsync(
                tableName: "http_request_queue",
                archiveTableName: "archived_http_request_queue",
                idColumn: "id",
                whereSql: "state = 'InFlight' AND COALESCE(locked_until_utc, updated_at_utc, started_at_utc, created_at_utc) < {0}",
                cutoff: now.AddHours(-Math.Max(1, opt.StaleInFlightHttpQueueRetentionHours)),
                opt,
                metricTableName: "http_request_queue",
                ct).ConfigureAwait(false)
            : 0;

        var httpQueueDeleted =
            completedHttpQueueDeleted +
            failedHttpQueueDeleted +
            staleQueuedHttpQueueDeleted +
            staleRetryHttpQueueDeleted +
            staleInFlightHttpQueueDeleted;

        return new DataRetentionRunResult
        {
            SucceededOutboxDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "outbox_messages",
                archiveTableName: "archived_outbox_messages",
                idColumn: "id",
                whereSql: "state = 'Succeeded' AND updated_at_utc < {0}",
                cutoff: now.AddDays(-opt.SucceededOutboxRetentionDays),
                opt,
                metricTableName: "outbox_messages",
                ct).ConfigureAwait(false),

            FailedOutboxDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "outbox_messages",
                archiveTableName: "archived_outbox_messages",
                idColumn: "id",
                whereSql: "state = 'Failed' AND updated_at_utc < {0}",
                cutoff: now.AddDays(-opt.FailedOutboxRetentionDays),
                opt,
                metricTableName: "outbox_messages",
                ct).ConfigureAwait(false),

            DeadLetterOutboxDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "outbox_messages",
                archiveTableName: "archived_outbox_messages",
                idColumn: "id",
                whereSql: "state = 'DeadLetter' AND updated_at_utc < {0}",
                cutoff: now.AddDays(-opt.DeadLetterOutboxRetentionDays),
                opt,
                metricTableName: "outbox_messages",
                ct).ConfigureAwait(false),

            InboxDeleted = await DeleteBatchesAsync(
                tableName: "inbox_messages",
                idColumn: "id",
                whereSql: "processed_at_utc < {0}",
                cutoff: now.AddDays(-opt.InboxRetentionDays),
                opt,
                metricTableName: "inbox_messages",
                ct).ConfigureAwait(false),

            BusJournalDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "bus_journal",
                archiveTableName: "archived_bus_journal",
                idColumn: "id",
                whereSql: "occurred_at_utc < {0}",
                cutoff: now.AddDays(-opt.BusJournalRetentionDays),
                opt,
                metricTableName: "bus_journal",
                ct).ConfigureAwait(false),

            CompletedHttpQueueDeleted = completedHttpQueueDeleted,
            FailedHttpQueueDeleted = failedHttpQueueDeleted,
            StaleQueuedHttpQueueDeleted = staleQueuedHttpQueueDeleted,
            StaleRetryHttpQueueDeleted = staleRetryHttpQueueDeleted,
            StaleInFlightHttpQueueDeleted = staleInFlightHttpQueueDeleted,
            HttpQueueDeleted = httpQueueDeleted,
        };
    }

    private async Task EnsureArchiveTablesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS archived_outbox_messages (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                archived_at_utc timestamptz NOT NULL,
                archive_reason varchar(64) NOT NULL,
                row_json jsonb NOT NULL
            );

            CREATE TABLE IF NOT EXISTS archived_bus_journal (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                archived_at_utc timestamptz NOT NULL,
                archive_reason varchar(64) NOT NULL,
                row_json jsonb NOT NULL
            );

            CREATE TABLE IF NOT EXISTS archived_http_request_queue (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                archived_at_utc timestamptz NOT NULL,
                archive_reason varchar(64) NOT NULL,
                row_json jsonb NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_archived_outbox_messages_archived_at
                ON archived_outbox_messages (archived_at_utc);

            CREATE INDEX IF NOT EXISTS ix_archived_bus_journal_archived_at
                ON archived_bus_journal (archived_at_utc);

            CREATE INDEX IF NOT EXISTS ix_archived_http_request_queue_archived_at
                ON archived_http_request_queue (archived_at_utc);

            CREATE INDEX IF NOT EXISTS ix_http_request_queue_retention_succeeded
                ON http_request_queue (completed_at_utc, updated_at_utc, created_at_utc)
                WHERE state = 'Succeeded';

            CREATE INDEX IF NOT EXISTS ix_http_request_queue_retention_failed
                ON http_request_queue (completed_at_utc, updated_at_utc, created_at_utc)
                WHERE state = 'Failed';

            CREATE INDEX IF NOT EXISTS ix_http_request_queue_retention_queued
                ON http_request_queue (created_at_utc, locked_until_utc)
                WHERE state = 'Queued';

            CREATE INDEX IF NOT EXISTS ix_http_request_queue_retention_retry
                ON http_request_queue (next_attempt_at_utc, updated_at_utc, created_at_utc, locked_until_utc)
                WHERE state = 'Retry';

            CREATE INDEX IF NOT EXISTS ix_http_request_queue_retention_inflight
                ON http_request_queue (locked_until_utc, updated_at_utc, started_at_utc, created_at_utc)
                WHERE state = 'InFlight';
            """,
            ct).ConfigureAwait(false);
    }

    private async Task<int> ArchiveThenDeleteBatchesAsync(
        string tableName,
        string archiveTableName,
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
            var deleted = await ArchiveThenDeleteBatchAsync(
                    tableName,
                    archiveTableName,
                    idColumn,
                    whereSql,
                    cutoff,
                    opt.BatchSize,
                    ct)
                .ConfigureAwait(false);

            if (deleted == 0)
            {
                break;
            }

            total += deleted;
            ArgusMeters.DataRetentionArchivedRows.Add(
                deleted,
                new KeyValuePair<string, object?>("table", metricTableName));
            ArgusMeters.DataRetentionDeletedRows.Add(
                deleted,
                new KeyValuePair<string, object?>("table", metricTableName));

            if (opt.DelayBetweenBatchesMs > 0)
            {
                await Task.Delay(opt.DelayBetweenBatchesMs, ct).ConfigureAwait(false);
            }
        }

        return total;
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
            {
                break;
            }

            total += deleted;
            ArgusMeters.DataRetentionDeletedRows.Add(
                deleted,
                new KeyValuePair<string, object?>("table", metricTableName));

            if (opt.DelayBetweenBatchesMs > 0)
            {
                await Task.Delay(opt.DelayBetweenBatchesMs, ct).ConfigureAwait(false);
            }
        }

        return total;
    }

    private async Task<int> ArchiveThenDeleteBatchAsync(
        string tableName,
        string archiveTableName,
        string idColumn,
        string whereSql,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sql =
            $"""
             WITH candidate AS (
                 SELECT {idColumn}
                 FROM {tableName}
                 WHERE {whereSql}
                 ORDER BY {idColumn}
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
             ),
             archived AS (
                 INSERT INTO {archiveTableName} (archived_at_utc, archive_reason, row_json)
                 SELECT now(), 'retention', to_jsonb(source_row)
                 FROM {tableName} source_row
                 JOIN candidate ON source_row.{idColumn} = candidate.{idColumn}
                 RETURNING 1
             )
             DELETE FROM {tableName} delete_row
             USING candidate
             WHERE delete_row.{idColumn} = candidate.{idColumn};
             """;

        return await db.Database.ExecuteSqlRawAsync(sql, new object[] { cutoff }, ct).ConfigureAwait(false);
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

        var sql =
            $"""
             WITH candidate AS (
                 SELECT {idColumn}
                 FROM {tableName}
                 WHERE {whereSql}
                 ORDER BY {idColumn}
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
             )
             DELETE FROM {tableName} delete_row
             USING candidate
             WHERE delete_row.{idColumn} = candidate.{idColumn};
             """;

        return await db.Database.ExecuteSqlRawAsync(sql, new object[] { cutoff }, ct).ConfigureAwait(false);
    }
}
