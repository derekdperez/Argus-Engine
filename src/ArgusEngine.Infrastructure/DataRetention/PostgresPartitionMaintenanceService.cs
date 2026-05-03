using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.DataRetention;

public sealed class PostgresPartitionMaintenanceService(
    IDbContextFactory<ArgusDbContext> dbFactory,
    ILogger<PostgresPartitionMaintenanceService> logger) : IPartitionMaintenanceService
{
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
            logger.LogInformation("bus_journal is not partitioned; skipping partition maintenance.");
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

            logger.LogInformation("Ensured bus_journal partition {PartitionName}.", name);
        }
    }
}
