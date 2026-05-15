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
            await ReconDbCommands.ExecuteAsync(
                db,
                """
                UPDATE recon_orchestrator_provider_runs
                SET status = 'completed',
                    completed_at_utc = @now,
                    emitted_subdomain_count = @emitted_subdomain_count,
                    last_error = NULL,
                    updated_at_utc = @now
                WHERE target_id = @target_id AND provider = @provider;
                """,
                new Dictionary<string, object?>
                {
                    ["target_id"] = targetId,
                    ["provider"] = NormalizeProvider(provider),
                    ["emitted_subdomain_count"] = emittedSubdomainCount,
                    ["now"] = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record recon provider completion for target {TargetId}, provider {Provider}.", targetId, provider);
        }
    }

    public async Task MarkProviderFailedAsync(
        Guid targetId,
        string provider,
        string errorMessage,
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
                    (id, target_id, provider, status, requested_at_utc, completed_at_utc, last_error, updated_at_utc)
                VALUES
                    (@id, @target_id, @provider, 'failed', @now, @now, @error, @now)
                ON CONFLICT (target_id, provider) DO UPDATE SET
                    status = 'failed',
                    completed_at_utc = EXCLUDED.completed_at_utc,
                    last_error = EXCLUDED.last_error,
                    updated_at_utc = EXCLUDED.updated_at_utc;
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid(),
                    ["target_id"] = targetId,
                    ["provider"] = NormalizeProvider(provider),
                    ["error"] = errorMessage.Length > 4096 ? errorMessage[..4096] : errorMessage,
                    ["now"] = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record recon provider failure for target {TargetId}, provider {Provider}.", targetId, provider);
        }
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant();
}
