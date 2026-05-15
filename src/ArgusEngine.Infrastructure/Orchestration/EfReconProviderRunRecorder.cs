using ArgusEngine.Application.Orchestration;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Orchestration;

public sealed class EfReconProviderRunRecorder(
    IDbContextFactory<ArgusDbContext> dbFactory,
    ILogger<EfReconProviderRunRecorder> logger) : IReconProviderRunRecorder
{
    public async Task MarkProviderStartedAsync(
        Guid targetId,
        string provider,
        Guid correlationId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
            await ReconDbCommands.ExecuteAsync(
                db,
                """
                INSERT INTO recon_orchestrator_provider_runs
                    (id, target_id, provider, status, requested_at_utc, started_at_utc, correlation_id, event_id, updated_at_utc)
                VALUES
                    (@id, @target_id, @provider, 'running', @now, @now, @correlation_id, @event_id, @now)
                ON CONFLICT (target_id, provider) DO UPDATE SET
                    status = 'running',
                    status_reason = NULL,
                    started_at_utc = COALESCE(recon_orchestrator_provider_runs.started_at_utc, EXCLUDED.started_at_utc),
                    correlation_id = COALESCE(recon_orchestrator_provider_runs.correlation_id, EXCLUDED.correlation_id),
                    event_id = COALESCE(recon_orchestrator_provider_runs.event_id, EXCLUDED.event_id),
                    updated_at_utc = EXCLUDED.updated_at_utc;
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid(),
                    ["target_id"] = targetId,
                    ["provider"] = NormalizeProvider(provider),
                    ["correlation_id"] = correlationId == Guid.Empty ? null : correlationId,
                    ["event_id"] = eventId == Guid.Empty ? null : eventId,
                    ["now"] = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record recon provider start for target {TargetId}, provider {Provider}.", targetId, provider);
        }
    }

    public async Task MarkProviderAwaitingAssetPersistenceAsync(
        Guid targetId,
        string provider,
        IReadOnlyCollection<string> emittedSubdomainKeys,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var normalizedProvider = NormalizeProvider(provider);
            var now = DateTimeOffset.UtcNow;
            var distinctKeys = emittedSubdomainKeys
                .Select(NormalizeSubdomainKey)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (distinctKeys.Length == 0)
            {
                await MarkProviderCompletedCoreAsync(db, targetId, normalizedProvider, 0, now, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var key in distinctKeys)
                {
                    await ReconDbCommands.ExecuteAsync(
                        db,
                        """
                        INSERT INTO recon_orchestrator_provider_discoveries
                            (id, target_id, provider, subdomain_key, status, discovered_at_utc, updated_at_utc)
                        VALUES
                            (@id, @target_id, @provider, @subdomain_key, 'awaiting_persistence', @now, @now)
                        ON CONFLICT (target_id, provider, subdomain_key) DO UPDATE SET
                            status = CASE
                                WHEN recon_orchestrator_provider_discoveries.status = 'persisted' THEN recon_orchestrator_provider_discoveries.status
                                ELSE 'awaiting_persistence'
                            END,
                            updated_at_utc = EXCLUDED.updated_at_utc;
                        """,
                        new Dictionary<string, object?>
                        {
                            ["id"] = Guid.NewGuid(),
                            ["target_id"] = targetId,
                            ["provider"] = normalizedProvider,
                            ["subdomain_key"] = key,
                            ["now"] = now
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await ReconDbCommands.ExecuteAsync(
                    db,
                    """
                    UPDATE recon_orchestrator_provider_runs
                    SET status = 'awaiting_asset_persistence',
                        emitted_subdomain_count = @emitted_subdomain_count,
                        status_reason = 'waiting_for_subdomain_assets_to_persist',
                        last_error = NULL,
                        updated_at_utc = @now
                    WHERE target_id = @target_id AND provider = @provider;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["target_id"] = targetId,
                        ["provider"] = normalizedProvider,
                        ["emitted_subdomain_count"] = distinctKeys.Length,
                        ["now"] = now
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record recon provider asset-persistence wait for target {TargetId}, provider {Provider}.", targetId, provider);
        }
    }

    public async Task MarkProviderCompletedAsync(
        Guid targetId,
        string provider,
        int emittedSubdomainCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
            await MarkProviderCompletedCoreAsync(
                db,
                targetId,
                NormalizeProvider(provider),
                emittedSubdomainCount,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record recon provider completion for target {TargetId}, provider {Provider}.", targetId, provider);
        }
    }

    public async Task MarkProviderSkippedAsync(
        Guid targetId,
        string provider,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await MarkTerminalAsync(targetId, provider, "skipped", reason, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkProviderFailedAsync(
        Guid targetId,
        string provider,
        string error,
        CancellationToken cancellationToken = default)
    {
        await MarkTerminalAsync(targetId, provider, "failed", error, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkProviderCompletedCoreAsync(
        ArgusDbContext db,
        Guid targetId,
        string provider,
        int emittedSubdomainCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await ReconDbCommands.ExecuteAsync(
            db,
            """
            UPDATE recon_orchestrator_provider_runs
            SET status = 'completed',
                completed_at_utc = @now,
                emitted_subdomain_count = @emitted_subdomain_count,
                status_reason = 'completed',
                last_error = NULL,
                updated_at_utc = @now
            WHERE target_id = @target_id AND provider = @provider;
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = targetId,
                ["provider"] = provider,
                ["emitted_subdomain_count"] = emittedSubdomainCount,
                ["now"] = now
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkTerminalAsync(
        Guid targetId,
        string provider,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
            await ReconDbCommands.ExecuteAsync(
                db,
                """
                INSERT INTO recon_orchestrator_provider_runs
                    (id, target_id, provider, status, requested_at_utc, completed_at_utc, status_reason, last_error, updated_at_utc)
                VALUES
                    (@id, @target_id, @provider, @status, @now, @now, @reason, @reason, @now)
                ON CONFLICT (target_id, provider) DO UPDATE SET
                    status = EXCLUDED.status,
                    completed_at_utc = EXCLUDED.completed_at_utc,
                    status_reason = EXCLUDED.status_reason,
                    last_error = EXCLUDED.last_error,
                    updated_at_utc = EXCLUDED.updated_at_utc;
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid(),
                    ["target_id"] = targetId,
                    ["provider"] = NormalizeProvider(provider),
                    ["status"] = status,
                    ["reason"] = string.IsNullOrWhiteSpace(reason) ? status : reason.Trim(),
                    ["now"] = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record recon provider terminal status for target {TargetId}, provider {Provider}.", targetId, provider);
        }
    }

    private static string NormalizeProvider(string provider) =>
        string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim().ToLowerInvariant();

    private static string NormalizeSubdomainKey(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('.').ToLowerInvariant();
}
