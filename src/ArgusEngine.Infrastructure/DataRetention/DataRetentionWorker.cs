using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.DataRetention;
using ArgusEngine.Infrastructure.Persistence.Data;
using ArgusEngine.Infrastructure.Observability;

namespace ArgusEngine.Infrastructure.DataRetention;

public sealed class DataRetentionWorker(
    IDbContextFactory<ArgusDbContext> dbFactory,
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
        await EnsureArchiveTablesAsync(ct).ConfigureAwait(false);

        var result = new DataRetentionRunResult
        {
            SucceededOutboxDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "outbox_messages",
                archiveTableName: "archived_outbox_messages",
                idColumn: "id",
                whereSql: "state = 'Succeeded' AND updated_at_utc < {0}",
                cutoff: DateTimeOffset.UtcNow.AddDays(-opt.SucceededOutboxRetentionDays),
                opt,
                metricTableName: "outbox_messages",
                ct).ConfigureAwait(false),

            FailedOutboxDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "outbox_messages",
                archiveTableName: "archived_outbox_messages",
                idColumn: "id",
                whereSql: "state = 'Failed' AND updated_at_utc < {0}",
                cutoff: DateTimeOffset.UtcNow.AddDays(-opt.FailedOutboxRetentionDays),
                opt,
                metricTableName: "outbox_messages",
                ct).ConfigureAwait(false),

            DeadLetterOutboxDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "outbox_messages",
                archiveTableName: "archived_outbox_messages",
                idColumn: "id",
                whereSql: "state = 'DeadLetter' AND updated_at_utc < {0}",
                cutoff: DateTimeOffset.UtcNow.AddDays(-opt.DeadLetterOutboxRetentionDays),
                opt,
                metricTableName: "outbox_messages",
                ct).ConfigureAwait(false),

            InboxDeleted = await DeleteBatchesAsync(
                tableName: "inbox_messages",
                idColumn: "id",
                whereSql: "processed_at_utc < {0}",
                cutoff: DateTimeOffset.UtcNow.AddDays(-opt.InboxRetentionDays),
                opt,
                metricTableName: "inbox_messages",
                ct).ConfigureAwait(false),

            BusJournalDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "bus_journal",
                archiveTableName: "archived_bus_journal",
                idColumn: "id",
                whereSql: "occurred_at_utc < {0}",
                cutoff: DateTimeOffset.UtcNow.AddDays(-opt.BusJournalRetentionDays),
                opt,
                metricTableName: "bus_journal",
                ct).ConfigureAwait(false),

            HttpQueueDeleted = await ArchiveThenDeleteBatchesAsync(
                tableName: "http_request_queue",
                archiveTableName: "archived_http_request_queue",
                idColumn: "id",
                whereSql: "state IN ('Succeeded', 'Failed') AND completed_at_utc < {0}",
                cutoff: DateTimeOffset.UtcNow.AddDays(-opt.HttpQueueRetentionDays),
                opt,
                metricTableName: "http_request_queue",
                ct).ConfigureAwait(false),
        };

        return result;
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
            var archived = await ArchiveBatchAsync(tableName, archiveTableName, idColumn, whereSql, cutoff, opt.BatchSize, ct)
                .ConfigureAwait(false);

            if (archived == 0)
                break;

            var deleted = await DeleteBatchAsync(tableName, idColumn, whereSql, cutoff, opt.BatchSize, ct)
                .ConfigureAwait(false);

            total += deleted;

            ArgusMeters.DataRetentionArchivedRows.Add(
                archived,
                new KeyValuePair<string, object?>("table", metricTableName));

            ArgusMeters.DataRetentionDeletedRows.Add(
                deleted,
                new KeyValuePair<string, object?>("table", metricTableName));

            if (opt.DelayBetweenBatchesMs > 0)
                await Task.Delay(opt.DelayBetweenBatchesMs, ct).ConfigureAwait(false);
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

    private async Task<int> ArchiveBatchAsync(
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
                SELECT *
                FROM {tableName}
                WHERE {whereSql}
                ORDER BY {idColumn}
                LIMIT {batchSize}
            )
            INSERT INTO {archiveTableName} (archived_at_utc, archive_reason, row_json)
            SELECT now(), 'retention', to_jsonb(candidate)
            FROM candidate;
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
