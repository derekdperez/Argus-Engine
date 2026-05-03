using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.DataRetention;

public sealed class PostgresPartitionMaintenanceService(
    IDbContextFactory<ArgusDbContext> dbFactory,
    ILogger<PostgresPartitionMaintenanceService> logger) : IPartitionMaintenanceService
{
    private static readonly Action<ILogger, Exception?> LogNotPartitioned =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(EnsurePartitionsAsync)), "bus_journal is not partitioned; skipping partition maintenance.");

    private static readonly Action<ILogger, string, Exception?> LogPartitionEnsured =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, nameof(EnsurePartitionsAsync)), "Ensured bus_journal partition {PartitionName}.");
    public async Task EnsurePartitionsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var isPartitioned = await db.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_partitioned_table pt
                    JOIN pg_class c ON c.oid = pt.partrelid
                    WHERE c.relname = 'bus_journal'
                )
                """)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        if (!isPartitioned)
        {
            LogNotPartitioned(logger, null);
            return;
        }

        var month = new DateTime(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 3; i++)
        {
            var start = month.AddMonths(i);
            var end = start.AddMonths(1);
            var name = $"bus_journal_{start:yyyy_MM}";

            await db.Database.ExecuteSqlRawAsync(
                $"""
                CREATE TABLE IF NOT EXISTS {name}
                PARTITION OF bus_journal
                FOR VALUES FROM ('{start:yyyy-MM-dd}') TO ('{end:yyyy-MM-dd}');

                CREATE INDEX IF NOT EXISTS ix_{name}_occurred_at_utc
                    ON {name} (occurred_at_utc);

                CREATE INDEX IF NOT EXISTS ix_{name}_message_type
                    ON {name} (message_type);
                """,
                ct).ConfigureAwait(false);

            LogPartitionEnsured(logger, name, null);
        }
    }
}
