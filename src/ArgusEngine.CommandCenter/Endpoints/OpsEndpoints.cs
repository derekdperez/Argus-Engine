using Microsoft.EntityFrameworkCore;

using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/ops/snapshot",
            async (ArgusDbContext db, IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
            {
                var snap = await OpsSnapshotBuilder.BuildAsync(db, httpFactory, configuration, ct).ConfigureAwait(false);
                return Results.Ok(snap);
            })
            .WithName("OpsSnapshot");

        app.MapGet(
            "/api/ops/rabbit-queues",
            async (IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
            {
                var (queues, _) = await OpsSnapshotBuilder.LoadRabbitQueuesAsync(httpFactory, configuration, ct).ConfigureAwait(false);
                return Results.Ok(queues);
            })
            .WithName("OpsRabbitQueues");

        app.MapGet(
            "/api/ops/overview",
            async (ArgusDbContext db, CancellationToken ct) =>
            {
                var totalTargets = await db.Targets.AsNoTracking()
                    .LongCountAsync(ct)
                    .ConfigureAwait(false);

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

                // The Operations page should report confirmed subdomains, not every discovered/queued subdomain.
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

                var publishedEventCount = await db.BusJournal.AsNoTracking()
                    .LongCountAsync(e => e.Direction == "Publish", ct)
                    .ConfigureAwait(false);

                // Top domain should count confirmed assets only.
                var domainCounts = await db.Assets.AsNoTracking()
                    .Where(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
                    .Join(db.Targets.AsNoTracking(), a => a.TargetId, t => t.Id, (_, t) => t.RootDomain)
                    .GroupBy(d => d)
                    .Select(g => new { RootDomain = g.Key, Count = g.LongCount() })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var top = domainCounts
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.RootDomain, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                var domains10OrMore = domainCounts.LongCount(x => x.Count >= 10);
                var domains10OrFewer = domainCounts.LongCount(x => x.Count < 10);

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
                        publishedEventCount));
            })
            .WithName("OpsOverview");

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

                var successRate = publishes <= 0 ? 1m : Math.Min(1m, consumes / (decimal)publishes);

                var queued = await db.HttpRequestQueue.AsNoTracking()
                    .LongCountAsync(q => q.State == HttpRequestQueueState.Queued, ct)
                    .ConfigureAwait(false);

                var readyRetry = await db.HttpRequestQueue.AsNoTracking()
                    .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now, ct)
                    .ConfigureAwait(false);

                var backlog = queued + readyRetry;

                var completed = await db.HttpRequestQueue.AsNoTracking()
                    .LongCountAsync(q => q.State == HttpRequestQueueState.Succeeded && q.CompletedAtUtc >= since, ct)
                    .ConfigureAwait(false);

                var failedLastHour = await db.HttpRequestQueue.AsNoTracking()
                    .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= since, ct)
                    .ConfigureAwait(false);

                var oldestQueuedAt = await db.HttpRequestQueue.AsNoTracking()
                    .Where(q => q.State == HttpRequestQueueState.Queued
                                || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now))
                    .OrderBy(q => q.CreatedAtUtc)
                    .Select(q => (DateTimeOffset?)q.CreatedAtUtc)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                var apiReady = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);

                return Results.Ok(
                    new ReliabilitySloSnapshotDto(
                        now,
                        publishes,
                        consumes,
                        successRate,
                        backlog,
                        oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds,
                        completed,
                        failedLastHour,
                        apiReady));
            })
            .WithName("ReliabilitySloSnapshot");

        app.MapGet(
            "/api/ops/docker-status",
            async (CancellationToken ct) =>
            {
                var snapshot = await DockerRuntimeStatusBuilder.BuildAsync(ct).ConfigureAwait(false);
                return Results.Ok(snapshot);
            })
            .WithName("DockerRuntimeStatus");

        return app;
    }

    public static void Map(WebApplication app) => app.MapOpsEndpoints();
}
