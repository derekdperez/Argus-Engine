using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class AssetAdmissionDecisionSchemaInitializer(
    IServiceProvider services,
    ILogger<AssetAdmissionDecisionSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS asset_admission_decisions (
                    id uuid PRIMARY KEY,
                    target_id uuid NOT NULL,
                    asset_id uuid NULL,
                    raw_value varchar(4096) NOT NULL,
                    canonical_key varchar(2048) NULL,
                    asset_kind varchar(64) NOT NULL,
                    decision varchar(64) NOT NULL,
                    reason_code varchar(128) NOT NULL,
                    reason_detail varchar(2048) NULL,
                    discovered_by varchar(128) NOT NULL,
                    discovery_context varchar(1024) NULL,
                    depth integer NOT NULL,
                    global_max_depth integer NOT NULL,
                    correlation_id uuid NOT NULL,
                    causation_id uuid NULL,
                    event_id uuid NULL,
                    occurred_at_utc timestamptz NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_asset_admission_decisions_target_id
                    ON asset_admission_decisions (target_id);

                CREATE INDEX IF NOT EXISTS ix_asset_admission_decisions_target_decision
                    ON asset_admission_decisions (target_id, decision);

                CREATE INDEX IF NOT EXISTS ix_asset_admission_decisions_target_reason
                    ON asset_admission_decisions (target_id, reason_code);

                CREATE INDEX IF NOT EXISTS ix_asset_admission_decisions_occurred_at
                    ON asset_admission_decisions (occurred_at_utc);

                CREATE INDEX IF NOT EXISTS ix_asset_admission_decisions_canonical_key
                    ON asset_admission_decisions (canonical_key);
                """,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
#pragma warning disable CA1848
            logger.LogWarning(ex, "Unable to ensure asset admission decision schema.");
#pragma warning restore CA1848
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
