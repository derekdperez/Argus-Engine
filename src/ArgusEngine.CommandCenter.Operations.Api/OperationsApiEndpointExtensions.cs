using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.Operations.Api;

internal static class OperationsApiEndpointExtensions
{
    public static IEndpointRouteBuilder MapOperationsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet(
                "/health/ready",
                async (ArgusDbContext db, CancellationToken ct) =>
                    await db.Database.CanConnectAsync(ct).ConfigureAwait(false)
                        ? Results.Ok(new { status = "ready", postgres = "ok" })
                        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();

        app.MapGet(
                "/api/status/summary",
                async (OperationsStatusService status, CancellationToken ct) =>
                    Results.Ok(await status.GetSnapshotAsync(ct).ConfigureAwait(false)))
            .WithName("GetOperationsStatusSummary")
            .WithTags("Status");

        app.MapGet(
                "/api/ops/snapshot",
                async (ArgusDbContext db, IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
                    Results.Ok(await OperationsSnapshotBuilder.BuildAsync(db, httpFactory, configuration, ct).ConfigureAwait(false)))
            .WithName("OperationsApiSnapshot")
            .WithTags("Operations");

        app.MapGet(
                "/api/ops/rabbit-queues",
                async (IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
                {
                    var (queues, _) = await OperationsSnapshotBuilder.LoadRabbitQueuesAsync(httpFactory, configuration, ct).ConfigureAwait(false);
                    return Results.Ok(queues);
                })
            .WithName("OperationsApiRabbitQueues")
            .WithTags("Operations");

        app.MapGet(
                "/api/ops/overview",
                async (
                    ArgusDbContext db,
                    IDbContextFactory<FileStoreDbContext> fileStoreFactory,
                    ILoggerFactory loggerFactory,
                    CancellationToken ct) =>
                {
                    try
                    {
                        var totalTargets = await db.Targets.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
                        var totalAssetsConfirmed = await db.Assets.AsNoTracking()
                            .LongCountAsync(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct)
                            .ConfigureAwait(false);
                        var totalUrls = await db.Assets.AsNoTracking()
                            .LongCountAsync(a => a.Kind == AssetKind.Url, ct)
                            .ConfigureAwait(false);
                        var urlsFromFetchedPages = await db.Assets.AsNoTracking()
                            .LongCountAsync(
                                a => a.Kind == AssetKind.Url
                                    && a.DiscoveredBy == "spider-worker"
                                    && EF.Functions.Like(a.DiscoveryContext, "Spider: link extracted from fetched page %"),
                                ct)
                            .ConfigureAwait(false);
                        var urlsFromScripts = await db.Assets.AsNoTracking()
                            .LongCountAsync(
                                a => a.Kind == AssetKind.Url
                                    && a.DiscoveredBy == "spider-worker"
                                    && (EF.Functions.ILike(a.DiscoveryContext, "%.js%")
                                        || EF.Functions.ILike(a.DiscoveryContext, "%javascript%")),
                                ct)
                            .ConfigureAwait(false);
                        var urlsGuessedWithWordlist = await db.Assets.AsNoTracking()
                            .LongCountAsync(a => a.Kind == AssetKind.Url && EF.Functions.ILike(a.DiscoveredBy, "hvpath:%"), ct)
                            .ConfigureAwait(false);
                        var subdomainsConfirmed = await db.Assets.AsNoTracking()
                            .LongCountAsync(a => a.Kind == AssetKind.Subdomain && a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct)
                            .ConfigureAwait(false);
                        var lastAssetCreatedAt = await db.Assets.AsNoTracking()
                            .OrderByDescending(a => a.DiscoveredAtUtc)
                            .Select(a => (DateTimeOffset?)a.DiscoveredAtUtc)
                            .FirstOrDefaultAsync(ct)
                            .ConfigureAwait(false);
                        var lastWorkerEventPublishedAt = await db.BusJournal.AsNoTracking()
                            .Where(e => e.Direction == "Publish")
                            .OrderByDescending(e => e.OccurredAtUtc)
                            .Select(e => (DateTimeOffset?)e.OccurredAtUtc)
                            .FirstOrDefaultAsync(ct)
                            .ConfigureAwait(false);
                        var queuedHttpAssets = await db.Assets.AsNoTracking()
                            .LongCountAsync(a => a.Kind == AssetKind.Url && a.LifecycleStatus == AssetLifecycleStatus.Queued, ct)
                            .ConfigureAwait(false);
                        var technologyObservationCount = await db.TechnologyObservations.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
                        var uniqueTechnologyObservationCount = await db.TechnologyObservations.AsNoTracking()
                            .GroupBy(o => new { o.TechnologyName, o.Version })
                            .LongCountAsync(ct)
                            .ConfigureAwait(false);
                        var uniqueLegacyTechnologyCount = await db.TechnologyDetections.AsNoTracking()
                            .GroupBy(d => new { d.TechnologyName, d.Version })
                            .LongCountAsync(ct)
                            .ConfigureAwait(false);
                        var highValueAssetCount = await db.HighValueFindings.AsNoTracking()
                            .Where(f => f.IsHighValue && f.SourceAssetId != null)
                            .Select(f => f.SourceAssetId!.Value)
                            .Distinct()
                            .LongCountAsync(ct)
                            .ConfigureAwait(false);
                        var httpRequestsSentLastMinute = await db.HttpRequestQueue.AsNoTracking()
                            .LongCountAsync(q => q.StartedAtUtc != null && q.StartedAtUtc >= DateTimeOffset.UtcNow.AddMinutes(-1), ct)
                            .ConfigureAwait(false);
                        var publishedEventCount = await db.BusJournal.AsNoTracking()
                            .LongCountAsync(e => e.Direction == "Publish", ct)
                            .ConfigureAwait(false);
                        var domainCounts = await db.Assets.AsNoTracking()
                            .Where(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
                            .Join(db.Targets.AsNoTracking(), a => a.TargetId, t => t.Id, (_, t) => t.RootDomain)
                            .GroupBy(d => d)
                            .Select(g => new { RootDomain = g.Key, Count = g.LongCount() })
                            .ToListAsync(ct)
                            .ConfigureAwait(false);

                        var top = domainCounts.OrderByDescending(x => x.Count).ThenBy(x => x.RootDomain, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                        var storage = await OpsStorageMetricsQuery.LoadAsync(db, fileStoreFactory, ct).ConfigureAwait(false);
                        var workerCount = await db.WorkerHeartbeats.AsNoTracking()
                            .Select(h => h.HostName)
                            .Distinct()
                            .LongCountAsync(ct)
                            .ConfigureAwait(false);
                        var componentVersion = Environment.GetEnvironmentVariable("ARGUS_COMPONENT_VERSION") ?? "unknown";
                        var buildTime = Environment.GetEnvironmentVariable("ARGUS_BUILD_TIME_UTC") ?? "unknown";
                        return Results.Ok(
                            new OpsOverviewDto(
                                totalTargets,
                                totalAssetsConfirmed,
                                totalUrls,
                                urlsFromFetchedPages,
                                urlsFromScripts,
                                urlsGuessedWithWordlist,
                                top?.RootDomain,
                                top?.Count ?? 0,
                                domainCounts.LongCount(x => x.Count >= 10),
                                domainCounts.LongCount(x => x.Count < 10),
                                subdomainsConfirmed,
                                lastAssetCreatedAt,
                                lastWorkerEventPublishedAt,
                                queuedHttpAssets,
                                technologyObservationCount,
                                publishedEventCount,
                                uniqueTechnologyObservationCount + uniqueLegacyTechnologyCount,
                                highValueAssetCount,
                                httpRequestsSentLastMinute,
                                storage.AssetMetadataBytes,
                                storage.HttpArtifactBytes,
                                storage.InlineHttpBytes,
                                storage.EventJournalBytes,
                                storage.TotalBytes,
                                workerCount,
                                componentVersion,
                                buildTime));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var logger = loggerFactory.CreateLogger("OperationsApiOverview");
                        logger.LogWarning(ex, "Operations overview query failed due to transient database pressure.");
                        return Results.Problem(
                            detail: "Operations overview is temporarily unavailable due to database pressure.",
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                })
            .WithName("OperationsApiOverview")
            .WithTags("Operations");

        app.MapGet(
                "/api/ops/reliability-slo",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var since = now.AddHours(-1);
                    var publishes = await db.BusJournal.AsNoTracking()
                        .LongCountAsync(e => e.Direction == "Publish" && e.OccurredAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var consumes = await db.BusJournal.AsNoTracking()
                        .LongCountAsync(e => e.Direction == "Consume" && e.OccurredAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var queued = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Queued, ct)
                        .ConfigureAwait(false);
                    var readyRetry = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now, ct)
                        .ConfigureAwait(false);
                    var oldestQueuedAt = await db.HttpRequestQueue.AsNoTracking()
                        .Where(q => q.State == HttpRequestQueueState.Queued || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now))
                        .OrderBy(q => q.CreatedAtUtc)
                        .Select(q => (DateTimeOffset?)q.CreatedAtUtc)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    var completed = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Succeeded && q.CompletedAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var failedLastHour = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= since, ct)
                        .ConfigureAwait(false);

                    return Results.Ok(
                        new ReliabilitySloSnapshotDto(
                            now,
                            publishes,
                            consumes,
                            publishes <= 0 ? 1m : Math.Min(1m, consumes / (decimal)publishes),
                            queued + readyRetry,
                            oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds,
                            completed,
                            failedLastHour,
                            await db.Database.CanConnectAsync(ct).ConfigureAwait(false)));
                })
            .WithName("OperationsApiReliabilitySlo")
            .WithTags("Operations");

        app.MapGet(
                "/api/ops/docker-status",
                async (CancellationToken ct) => Results.Ok(await DockerRuntimeStatusBuilder.BuildAsync(ct).ConfigureAwait(false)))
            .WithName("OperationsApiDockerRuntimeStatus")
            .WithTags("Operations");

        app.MapGet("/api/ops/recent-events", async (ArgusDbContext db, CancellationToken ct) =>
        {
            var events = await db.BusJournal.AsNoTracking()
                .OrderByDescending(e => e.OccurredAtUtc)
                .Take(30)
                .Select(e => new
                {
                    e.Id,
                    e.Direction,
                    e.MessageType,
                    e.OccurredAtUtc,
                    e.HostName,
                    e.Status,
                    e.DurationMs,
                    e.Error
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(events);
        }).WithName("OpsRecentEvents");

        app.MapGet("/api/ops/target-enrichment", async (ArgusDbContext db, int? take, CancellationToken ct) =>
        {
            var limit = Math.Clamp(take ?? 250, 1, 500);

            List<OrchestratorStateRow> orchestratorStates;
            List<ProviderRunRow> providerRuns;
            try
            {
                orchestratorStates = await db.Database.SqlQuery<OrchestratorStateRow>(
                    $"""SELECT s.target_id AS TargetId, s.status AS OrchestratorStatus, s.attached_at_utc AS AttachedAtUtc FROM recon_orchestrator_states s ORDER BY s.updated_at_utc DESC LIMIT {limit}""")
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                providerRuns = await db.Database.SqlQuery<ProviderRunRow>(
                    $"""SELECT DISTINCT ON (target_id) target_id AS TargetId, provider, status FROM recon_orchestrator_provider_runs ORDER BY target_id, updated_at_utc DESC LIMIT {limit}""")
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return Results.Ok(Array.Empty<object>());
            }

            var enrichment = orchestratorStates.Select(o =>
            {
                var runs = providerRuns.Where(r => r.TargetId == o.TargetId).ToList();
                var hasEnumeration = runs.Any(r => r.Provider is "subfinder" or "amass" && r.Status == "completed");
                var hasSpider = runs.Any(r => r.Provider is "spider" && r.Status == "completed");
                return new
                {
                    targetId = o.TargetId,
                    orchestratorStatus = o.OrchestratorStatus,
                    hasSubdomainsEnumerated = hasEnumeration,
                    hasBeenSpidered = hasSpider
                };
            }).ToList();

            return Results.Ok(enrichment);
        }).WithName("OpsTargetEnrichment");

        return app;
    }

    private sealed record OrchestratorStateRow(Guid TargetId, string OrchestratorStatus, DateTimeOffset AttachedAtUtc);
    private sealed record ProviderRunRow(Guid TargetId, string Provider, string Status);
}
