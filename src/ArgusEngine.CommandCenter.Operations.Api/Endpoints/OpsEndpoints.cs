using Microsoft.EntityFrameworkCore;

using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Operations.Api;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.Operations.Api.Endpoints;

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/ops/snapshot",
            async (ArgusDbContext db, IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
            {
                var snap = await OperationsSnapshotBuilder.BuildAsync(db, httpFactory, configuration, ct).ConfigureAwait(false);
                return Results.Ok(snap);
            })
            .WithName("OpsSnapshot");

        app.MapGet(
            "/api/ops/rabbit-queues",
            async (IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
            {
                var (queues, _) = await OperationsSnapshotBuilder.LoadRabbitQueuesAsync(httpFactory, configuration, ct).ConfigureAwait(false);
                return Results.Ok(queues);
            })
            .WithName("OpsRabbitQueues");

        app.MapGet(
            "/api/ops/overview",
            async (ArgusDbContext db, IDbContextFactory<FileStoreDbContext> fileStoreFactory, IConfiguration configuration, CancellationToken ct) =>
            {
                var explicitTargetCount = await db.Targets.AsNoTracking()
                    .LongCountAsync(ct)
                    .ConfigureAwait(false);

                var assetTargetCount = await db.Assets.AsNoTracking()
                    .Select(a => a.TargetId)
                    .Distinct()
                    .LongCountAsync(ct)
                    .ConfigureAwait(false);

                // If target rows were lost during an earlier bad restore/merge, keep the Ops dashboard
                // truthful by falling back to distinct TargetId values already attached to stored assets.
                var totalTargets = Math.Max(explicitTargetCount, assetTargetCount);

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
                    .LongCountAsync(
                        a => a.Kind == AssetKind.Url
                            && EF.Functions.ILike(a.DiscoveredBy, "hvpath:%"),
                        ct)
                    .ConfigureAwait(false);

                var subdomainsConfirmed = await db.Assets.AsNoTracking()
                    .LongCountAsync(
                        a => a.Kind == AssetKind.Subdomain
                            && a.LifecycleStatus == AssetLifecycleStatus.Confirmed,
                        ct)
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

                var technologyObservationCount = await db.TechnologyObservations.AsNoTracking()
                    .LongCountAsync(ct)
                    .ConfigureAwait(false);

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
                    .LongCountAsync(
                        q => q.StartedAtUtc != null && q.StartedAtUtc >= DateTimeOffset.UtcNow.AddMinutes(-1),
                        ct)
                    .ConfigureAwait(false);

                var publishedEventCount = await db.BusJournal.AsNoTracking()
                    .LongCountAsync(e => e.Direction == "Publish", ct)
                    .ConfigureAwait(false);

                var domainCounts = await db.Assets.AsNoTracking()
                    .Where(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
                    .GroupBy(a => a.TargetId)
                    .Select(g => new
                    {
                        TargetId = g.Key,
                        Count = g.LongCount(),
                    })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var targetNames = await db.Targets.AsNoTracking()
                    .Select(t => new { t.Id, t.RootDomain })
                    .ToDictionaryAsync(t => t.Id, t => t.RootDomain, ct)
                    .ConfigureAwait(false);

                var top = domainCounts
                    .Select(x => new
                    {
                        RootDomain = targetNames.TryGetValue(x.TargetId, out var root) ? root : x.TargetId.ToString(),
                        x.Count,
                    })
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.RootDomain, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                var domains10OrMore = domainCounts.LongCount(x => x.Count >= 10);
                var domains10OrFewer = domainCounts.LongCount(x => x.Count < 10);

                var storage = await OpsStorageMetricsQuery.LoadAsync(db, fileStoreFactory, ct).ConfigureAwait(false);

                var workerCount = await db.WorkerHeartbeats.AsNoTracking()
                    .Select(h => h.HostName)
                    .Distinct()
                    .LongCountAsync(ct)
                    .ConfigureAwait(false);

                var componentVersion = Environment.GetEnvironmentVariable("ARGUS_COMPONENT_VERSION")
                    ?? configuration["ARGUS_COMPONENT_VERSION"]
                    ?? "unknown";
                var buildTime = Environment.GetEnvironmentVariable("ARGUS_BUILD_TIME_UTC")
                    ?? configuration["ARGUS_BUILD_TIME_UTC"]
                    ?? "unknown";

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
                        domains10OrMore,
                        domains10OrFewer,
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
            })
            .WithName("OpsOverview");

        return app;
    }

    public static void Map(WebApplication app) => app.MapOpsEndpoints();
}

public static class StorageOverviewEndpoints
{
    public static IEndpointRouteBuilder MapStorageOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/ops/storage-overview",
            async (ArgusDbContext db, CancellationToken ct) =>
            {
                var totalAssets = await db.Assets.LongCountAsync(ct).ConfigureAwait(false);

                var byKind = await db.Assets.AsNoTracking()
                    .GroupBy(a => a.Kind)
                    .Select(g => new { Kind = g.Key.ToString(), Count = g.LongCount() })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var byTarget = await db.Assets.AsNoTracking()
                    .GroupBy(a => a.TargetId)
                    .Select(g => new
                    {
                        TargetId = g.Key,
                        Count = g.LongCount(),
                        Subdomains = g.LongCount(a => a.Kind == AssetKind.Subdomain),
                        Urls = g.LongCount(a => a.Kind == AssetKind.Url)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(20)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var targetIds = byTarget.Select(x => x.TargetId).ToList();
                var targetRoots = await db.Targets.AsNoTracking()
                    .Where(t => targetIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, t => t.RootDomain, ct)
                    .ConfigureAwait(false);

                var topDomains = byTarget.Select(x => new
                {
                    Domain = targetRoots.GetValueOrDefault(x.TargetId, x.TargetId.ToString()),
                    Total = x.Count,
                    Subdomains = x.Subdomains,
                    Urls = x.Urls
                }).ToList();

                var queueStats = await db.HttpRequestQueue.AsNoTracking()
                    .GroupBy(q => q.State)
                    .Select(g => new { State = g.Key.ToString(), Count = g.LongCount() })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                return Results.Ok(new
                {
                    totalAssets,
                    byKind,
                    topDomains,
                    queueStats
                });
            })
            .WithName("StorageOverview");

        return app;
    }

    public static void Map(WebApplication app) => app.MapStorageOverviewEndpoints();
}
