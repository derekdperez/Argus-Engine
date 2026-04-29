using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightmareV2.Application.Workers;
using NightmareV2.CommandCenter;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Contracts;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class WorkerOpsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/workers",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var rows = await db.WorkerSwitches.AsNoTracking()
                        .OrderBy(w => w.WorkerKey)
                        .Select(w => new WorkerSwitchDto(w.WorkerKey, w.IsEnabled, w.UpdatedAtUtc))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("ListWorkers");

        app.MapGet(
                "/api/workers/capabilities",
                () =>
                {
                    var rows = new[]
                    {
                        new WorkerCapabilityDto(WorkerKeys.Gatekeeper, "Gatekeeper", "v1", true, true, false, false),
                        new WorkerCapabilityDto(WorkerKeys.Spider, "Spider HTTP Queue", "v1", false, true, true, false),
                        new WorkerCapabilityDto(WorkerKeys.Enumeration, "Enumeration", "v1", true, true, true, false),
                        new WorkerCapabilityDto(WorkerKeys.PortScan, "Port Scan", "v1", true, true, true, false),
                        new WorkerCapabilityDto(WorkerKeys.HighValueRegex, "High Value Regex", "v1", true, false, false, false),
                        new WorkerCapabilityDto(WorkerKeys.HighValuePaths, "High Value Paths", "v1", true, true, false, false),
                    };
                    return Results.Ok(rows);
                })
            .WithName("WorkerCapabilities");

        app.MapGet(
                "/api/workers/health",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var since1 = now.AddHours(-1);
                    var since24 = now.AddHours(-24);

                    var toggles = await db.WorkerSwitches.AsNoTracking()
                        .ToDictionaryAsync(w => w.WorkerKey, w => w.IsEnabled, ct)
                        .ConfigureAwait(false);

                    var consumeRows = await db.BusJournal.AsNoTracking()
                        .Where(e => e.Direction == "Consume" && e.ConsumerType != null && e.OccurredAtUtc >= since24)
                        .Select(e => new { e.ConsumerType, e.OccurredAtUtc })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var byKind = consumeRows
                        .Select(r => new { Kind = WorkerConsumerKindResolver.KindFromConsumerType(r.ConsumerType), r.OccurredAtUtc })
                        .Where(r => !string.IsNullOrWhiteSpace(r.Kind))
                        .GroupBy(r => r.Kind!)
                        .ToDictionary(
                            g => g.Key,
                            g => new
                            {
                                Last = g.Max(x => x.OccurredAtUtc),
                                Last1h = g.LongCount(x => x.OccurredAtUtc >= since1),
                                Last24h = g.LongCount(),
                            },
                            StringComparer.Ordinal);

                    var spiderRows = await db.HttpRequestQueue.AsNoTracking()
                        .Where(q => q.StartedAtUtc != null && q.StartedAtUtc >= since24)
                        .Select(q => q.StartedAtUtc!.Value)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    if (spiderRows.Count > 0)
                    {
                        byKind[WorkerKeys.Spider] = new
                        {
                            Last = spiderRows.Max(),
                            Last1h = spiderRows.LongCount(t => t >= since1),
                            Last24h = spiderRows.LongCount(),
                        };
                    }

                    var keys = new[]
                    {
                        WorkerKeys.Gatekeeper,
                        WorkerKeys.Spider,
                        WorkerKeys.Enumeration,
                        WorkerKeys.PortScan,
                        WorkerKeys.HighValueRegex,
                        WorkerKeys.HighValuePaths,
                    };

                    var rows = keys.Select(
                            key =>
                            {
                                var enabled = toggles.GetValueOrDefault(key, true);
                                var has = byKind.TryGetValue(key, out var stats);
                                var last = has ? stats!.Last : (DateTimeOffset?)null;
                                var c1 = has ? stats!.Last1h : 0;
                                var c24 = has ? stats!.Last24h : 0;
                                var healthy = !enabled || c1 > 0 || (last is not null && (now - last.Value) <= TimeSpan.FromMinutes(15));
                                var reason = !enabled
                                    ? "worker toggle is disabled"
                                    : healthy
                                        ? (key == WorkerKeys.Spider ? "spider processed HTTP queue rows recently" : "worker consumed events recently")
                                        : (key == WorkerKeys.Spider ? "spider has no recent HTTP queue activity" : "worker has no recent consume activity");
                                return new WorkerHealthDto(key, enabled, last, c1, c24, healthy, reason);
                            })
                        .ToList();

                    return Results.Ok(rows);
                })
            .WithName("WorkerHealth");

        app.MapGet(
                "/api/workers/activity",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var snap = await WorkerActivityQuery.BuildSnapshotAsync(db, ct).ConfigureAwait(false);
                    return Results.Ok(snap);
                })
            .WithName("WorkerActivity");

        app.MapGet(
                "/api/ops/snapshot",
                async (NightmareDbContext db, IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
                {
                    var snap = await OpsSnapshotBuilder.BuildAsync(db, httpFactory, configuration, ct).ConfigureAwait(false);
                    return Results.Ok(snap);
                })
            .WithName("OpsSnapshot");

        app.MapGet(
                "/api/ops/overview",
                async (NightmareDbContext db, CancellationToken ct) =>
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
                        .LongCountAsync(
                            a => a.Kind == AssetKind.Url
                                && EF.Functions.ILike(a.DiscoveredBy, "hvpath:%"),
                            ct)
                        .ConfigureAwait(false);

                    var domainCounts = await db.Assets.AsNoTracking()
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
                            domains10OrFewer));
                })
            .WithName("OpsOverview");

        app.MapGet(
                "/api/ops/reliability-slo",
                async (NightmareDbContext db, CancellationToken ct) =>
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
                "/api/ops/reliability-baseline",
                async (NightmareDbContext db, IOptions<ReliabilityBudgetOptions> budgetOptions, CancellationToken ct) =>
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
                    var scheduledRetry = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc > now, ct)
                        .ConfigureAwait(false);
                    var inFlight = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.InFlight, ct)
                        .ConfigureAwait(false);
                    var failed = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed, ct)
                        .ConfigureAwait(false);
                    var completedLastHour = await db.HttpRequestQueue.AsNoTracking()
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

                    var queueMetrics = new HttpRequestQueueMetricsDto(
                        queued,
                        readyRetry,
                        scheduledRetry,
                        inFlight,
                        failed,
                        completedLastHour,
                        queued + readyRetry,
                        oldestQueuedAt,
                        oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds);

                    var budgets = budgetOptions.Value;
                    var serviceBudgets = budgets.ServiceNames
                        .Select(
                            name => new ServiceReliabilityBudgetDto(
                                name,
                                budgets.MinEventProcessingSuccessRate,
                                budgets.MaxQueueBacklogAgeSeconds,
                                budgets.MinQueueDrainPerHour,
                                budgets.MaxWorkerErrorsPerHour))
                        .ToList();

                    var breaches = new List<string>(capacity: 4);
                    if (successRate < budgets.MinEventProcessingSuccessRate)
                        breaches.Add($"event success rate {successRate:P2} below target {budgets.MinEventProcessingSuccessRate:P2}");
                    if (queueMetrics.OldestQueuedAgeSeconds.GetValueOrDefault() > budgets.MaxQueueBacklogAgeSeconds)
                        breaches.Add($"queue backlog age {queueMetrics.OldestQueuedAgeSeconds}s exceeds {budgets.MaxQueueBacklogAgeSeconds}s");
                    if (completedLastHour < budgets.MinQueueDrainPerHour)
                        breaches.Add($"queue drain {completedLastHour}/h below {budgets.MinQueueDrainPerHour}/h");
                    if (failedLastHour > budgets.MaxWorkerErrorsPerHour)
                        breaches.Add($"worker errors {failedLastHour}/h exceed {budgets.MaxWorkerErrorsPerHour}/h");

                    var apiReady = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
                    var snapshot = new ReliabilityBaselineSnapshotDto(
                        now,
                        queueMetrics,
                        publishes,
                        consumes,
                        successRate,
                        completedLastHour,
                        failedLastHour,
                        apiReady,
                        serviceBudgets,
                        RollbackRecommended: breaches.Count > 0,
                        Breaches: breaches);

                    return Results.Ok(snapshot);
                })
            .WithName("ReliabilityBaselineSnapshot");

        app.MapPut(
                "/api/workers/{key}",
                async (string key, WorkerPatchRequest body, NightmareDbContext db, CancellationToken ct) =>
                {
                    var row = await db.WorkerSwitches.FirstOrDefaultAsync(w => w.WorkerKey == key, ct).ConfigureAwait(false);
                    if (row is null)
                        return Results.NotFound();
                    row.IsEnabled = body.Enabled;
                    row.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.NoContent();
                })
            .WithName("PatchWorker");
    }
}
