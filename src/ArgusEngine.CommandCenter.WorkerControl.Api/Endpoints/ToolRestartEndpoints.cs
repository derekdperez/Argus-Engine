using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.WorkerControl.Api.Services;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

public static class ToolRestartEndpoints
{
    public static IEndpointRouteBuilder MapToolRestartEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/api/ops/subdomain-enum/restart",
                async (
                    RestartToolRequest body,
                    ArgusDbContext db,
                    IEventOutbox outbox,
                    IOptions<SubdomainEnumerationOptions> options,
                    IConfiguration configuration,
                    ILogger<ToolRestartEndpointsLogger> logger,
                    CancellationToken ct) =>
                {
                    logger.LogInformation(
                        "Subdomain enumeration restart requested. AllTargets: {AllTargets}, TargetCount: {TargetIdsCount}",
                        body.AllTargets,
                        body.TargetIds?.Length ?? 0);

                    var targets = await ResolveTargetsAsync(body, db, logger, ct).ConfigureAwait(false);

                    var providers = options.Value.DefaultProviders
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (providers.Length == 0)
                    {
                        providers = ["subfinder", "amass"];
                    }

                    var queued = 0;
                    var eventsToEnqueue = new List<SubdomainEnumerationRequested>();

                    foreach (var target in targets)
                    {
                        var correlation = NewId.NextGuid();

                        foreach (var provider in providers)
                        {
                            var eventId = NewId.NextGuid();

                            eventsToEnqueue.Add(
                                new SubdomainEnumerationRequested(
                                    target.Id,
                                    target.RootDomain,
                                    provider,
                                    "command-center-manual-restart",
                                    DateTimeOffset.UtcNow,
                                    correlation,
                                    EventId: eventId,
                                    CausationId: correlation,
                                    Producer: "command-center"));

                            queued++;
                        }
                    }

                    if (eventsToEnqueue.Count > 0)
                    {
                        await outbox.EnqueueBatchAsync(eventsToEnqueue, ct).ConfigureAwait(false);
                    }

                    var workerScale = await EnsureWorkersAvailableAsync(
                            configuration,
                            logger,
                            ct,
                            ("worker-enum", 1))
                        .ConfigureAwait(false);

                    logger.LogInformation(
                        "Subdomain enumeration restart completed. Targets={TargetCount}, JobsQueued={JobsQueued}, WorkerScale={WorkerScale}",
                        targets.Count,
                        queued,
                        workerScale.Message);

                    return Results.Ok(
                        new
                        {
                            Targets = targets.Count,
                            JobsQueued = queued,
                            WorkerScale = workerScale.Message,
                            WorkerScaleSucceeded = workerScale.Succeeded
                        });
                })
            .WithName("RestartSubdomainEnumeration");

        app.MapPost(
                "/api/ops/spider/restart",
                async (
                    RestartToolRequest body,
                    ArgusDbContext db,
                    RootSpiderSeedService rootSpiderSeedService,
                    IConfiguration configuration,
                    ILogger<ToolRestartEndpointsLogger> logger,
                    CancellationToken ct) =>
                {
                    logger.LogInformation(
                        "Spider restart requested. AllTargets: {AllTargets}, TargetCount: {TargetIdsCount}",
                        body.AllTargets,
                        body.TargetIds?.Length ?? 0);

                    var targets = await ResolveTargetsAsync(body, db, logger, ct).ConfigureAwait(false);
                    var targetIds = targets.Select(t => t.Id).ToHashSet();

                    var now = DateTimeOffset.UtcNow;

                    var existingQueueRows = await db.HttpRequestQueue
                        .Where(q => targetIds.Contains(q.TargetId) && q.State != HttpRequestQueueState.Succeeded)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    foreach (var row in existingQueueRows)
                    {
                        row.State = HttpRequestQueueState.Queued;
                        row.LockedBy = null;
                        row.LockedUntilUtc = null;
                        row.StartedAtUtc = null;
                        row.CompletedAtUtc = null;
                        row.LastError = null;
                        row.UpdatedAtUtc = now;
                        row.NextAttemptAtUtc = now;
                    }

                    var queuedRootSeeds = 0;

                    foreach (var target in targets)
                    {
                        var correlation = NewId.NextGuid();

                        var queued = await rootSpiderSeedService.QueueRootSpiderSeedsAsync(
                                target.Id,
                                target.RootDomain,
                                target.GlobalMaxDepth,
                                now,
                                correlation,
                                correlation,
                                ct)
                            .ConfigureAwait(false);

                        queuedRootSeeds += queued;
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    var workerScale = await EnsureWorkersAvailableAsync(
                            configuration,
                            logger,
                            ct,
                            ("worker-spider", 1),
                            ("worker-http-requester", 1))
                        .ConfigureAwait(false);

                    logger.LogInformation(
                        "Spider restart completed. Targets={TargetCount}, Existing={ExistingCount}, Seeds={RootSeedsCount}, WorkerScale={WorkerScale}",
                        targets.Count,
                        existingQueueRows.Count,
                        queuedRootSeeds,
                        workerScale.Message);

                    return Results.Ok(
                        new
                        {
                            Targets = targets.Count,
                            RequeuedExistingRequests = existingQueueRows.Count,
                            RootSeedsQueued = queuedRootSeeds,
                            WorkerScale = workerScale.Message,
                            WorkerScaleSucceeded = workerScale.Succeeded
                        });
                })
            .WithName("RestartSpidering");

        app.MapPost(
                "/api/ops/spider/continuous",
                async (
                    RestartToolRequest body,
                    ArgusDbContext db,
                    RootSpiderSeedService rootSpiderSeedService,
                    IConfiguration configuration,
                    ILogger<ToolRestartEndpointsLogger> logger,
                    CancellationToken ct) =>
                {
                    logger.LogInformation(
                        "Spider continuous requested. AllTargets: {AllTargets}, TargetCount: {TargetIdsCount}",
                        body.AllTargets,
                        body.TargetIds?.Length ?? 0);

                    var targets = await ResolveTargetsAsync(body, db, logger, ct).ConfigureAwait(false);

                    var now = DateTimeOffset.UtcNow;
                    var queuedRootSeeds = 0;

                    foreach (var target in targets)
                    {
                        var correlation = NewId.NextGuid();

                        var queued = await rootSpiderSeedService.QueueRootSpiderSeedsAsync(
                                target.Id,
                                target.RootDomain,
                                target.GlobalMaxDepth,
                                now,
                                correlation,
                                correlation,
                                ct)
                            .ConfigureAwait(false);

                        queuedRootSeeds += queued;
                    }

                    var workerScale = await EnsureWorkersAvailableAsync(
                            configuration,
                            logger,
                            ct,
                            ("worker-spider", 1),
                            ("worker-http-requester", 1))
                        .ConfigureAwait(false);

                    return Results.Ok(
                        new
                        {
                            Targets = targets.Count,
                            RootSeedsQueued = queuedRootSeeds,
                            WorkerScale = workerScale.Message,
                            WorkerScaleSucceeded = workerScale.Succeeded
                        });
                })
            .WithName("ContinuousSpider");

        app.MapPost(
                "/api/ops/subdomain-enum/continuous",
                async (
                    ArgusDbContext db,
                    IEventOutbox outbox,
                    IOptions<SubdomainEnumerationOptions> options,
                    IConfiguration configuration,
                    ILogger<ToolRestartEndpointsLogger> logger,
                    CancellationToken ct) =>
                {
                    logger.LogInformation("Subdomain enumeration continuous requested.");

                    var targets = await ResolveTargetsAsync(
                            new RestartToolRequest(null, AllTargets: true),
                            db,
                            logger,
                            ct)
                        .ConfigureAwait(false);

                    var providers = options.Value.DefaultProviders
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (providers.Length == 0)
                    {
                        providers = ["subfinder", "amass"];
                    }

                    var queued = 0;
                    var eventsToEnqueue = new List<SubdomainEnumerationRequested>();

                    foreach (var target in targets)
                    {
                        var correlation = NewId.NextGuid();

                        foreach (var provider in providers)
                        {
                            eventsToEnqueue.Add(
                                new SubdomainEnumerationRequested(
                                    target.Id,
                                    target.RootDomain,
                                    provider,
                                    "command-center-continuous",
                                    DateTimeOffset.UtcNow,
                                    correlation,
                                    EventId: NewId.NextGuid(),
                                    CausationId: correlation,
                                    Producer: "command-center"));

                            queued++;
                        }
                    }

                    if (eventsToEnqueue.Count > 0)
                    {
                        await outbox.EnqueueBatchAsync(eventsToEnqueue, ct).ConfigureAwait(false);
                    }

                    var workerScale = await EnsureWorkersAvailableAsync(
                            configuration,
                            logger,
                            ct,
                            ("worker-enum", 1))
                        .ConfigureAwait(false);

                    return Results.Ok(
                        new
                        {
                            Targets = targets.Count,
                            JobsQueued = queued,
                            WorkerScale = workerScale.Message,
                            WorkerScaleSucceeded = workerScale.Succeeded
                        });
                })
            .WithName("ContinuousSubdomainEnumeration");

        return app;
    }

    private static async Task<List<ReconTarget>> ResolveTargetsAsync(
        RestartToolRequest request,
        ArgusDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        var requestedIds = ParseTargetIds(request.TargetIds);

        var targetsQuery = db.Targets.AsQueryable();

        if (!request.AllTargets)
        {
            if (requestedIds.Count == 0)
            {
                return [];
            }

            targetsQuery = targetsQuery.Where(t => requestedIds.Contains(t.Id));
        }

        var targets = await targetsQuery
            .OrderBy(t => t.RootDomain)
            .Take(5000)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var missingIds = request.AllTargets
            ? null
            : requestedIds.Except(targets.Select(t => t.Id)).ToHashSet();

        if (request.AllTargets || (missingIds is { Count: > 0 }))
        {
            var fallbackTargets = await LoadFallbackTargetsFromStoredAssetsAsync(
                    db,
                    request.AllTargets ? null : missingIds,
                    ct)
                .ConfigureAwait(false);

            var knownIds = targets.Select(t => t.Id).ToHashSet();

            foreach (var fallback in fallbackTargets)
            {
                if (knownIds.Contains(fallback.Id))
                {
                    continue;
                }

                var entity = new ReconTarget
                {
                    Id = fallback.Id,
                    RootDomain = fallback.RootDomain,
                    GlobalMaxDepth = fallback.GlobalMaxDepth,
                    CreatedAtUtc = fallback.CreatedAtUtc
                };

                db.Targets.Add(entity);
                targets.Add(entity);
                knownIds.Add(entity.Id);
            }

            if (fallbackTargets.Count > 0)
            {
                try
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateException ex)
                {
                    logger.LogWarning(ex, "One or more fallback recon target rows could not be inserted; reloading target list.");
                    db.ChangeTracker.Clear();

                    targets = await targetsQuery
                        .OrderBy(t => t.RootDomain)
                        .Take(5000)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                }
            }
        }

        if (!request.AllTargets && requestedIds.Count > 0)
        {
            targets = targets
                .Where(t => requestedIds.Contains(t.Id))
                .OrderBy(t => t.RootDomain, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return targets
            .Where(t => !string.IsNullOrWhiteSpace(t.RootDomain))
            .Take(5000)
            .ToList();
    }

    private static async Task<List<FallbackTarget>> LoadFallbackTargetsFromStoredAssetsAsync(
        ArgusDbContext db,
        IReadOnlySet<Guid>? targetIds,
        CancellationToken ct)
    {
        var query = db.Assets.AsNoTracking();

        if (targetIds is not null)
        {
            query = query.Where(a => targetIds.Contains(a.TargetId));
        }

        var rows = await query
            .Where(a =>
                a.Kind == AssetKind.Target ||
                a.Kind == AssetKind.Domain ||
                a.Kind == AssetKind.Subdomain)
            .Select(a => new
            {
                a.TargetId,
                a.Kind,
                a.RawValue,
                a.CanonicalKey,
                a.DisplayName,
                a.Depth,
                a.DiscoveredAtUtc
            })
            .Take(25_000)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(a => a.TargetId)
            .Select(group =>
            {
                var preferred = group
                    .OrderBy(a => a.Kind == AssetKind.Target ? 0 : a.Kind == AssetKind.Domain ? 1 : 2)
                    .ThenBy(a => a.Depth)
                    .ThenBy(a => a.DiscoveredAtUtc)
                    .FirstOrDefault();

                var rootDomain = NormalizeRootDomain(
                    preferred?.RawValue,
                    preferred?.DisplayName,
                    preferred?.CanonicalKey,
                    preferred?.Kind ?? AssetKind.Target);

                if (string.IsNullOrWhiteSpace(rootDomain))
                {
                    return null;
                }

                return new FallbackTarget(
                    group.Key,
                    rootDomain,
                    Math.Clamp(group.Max(a => a.Depth) + 10, 1, 50),
                    group.Min(a => a.DiscoveredAtUtc));
            })
            .Where(t => t is not null)
            .Select(t => t!)
            .OrderBy(t => t.RootDomain, StringComparer.OrdinalIgnoreCase)
            .Take(5000)
            .ToList();
    }

    private static string NormalizeRootDomain(
        string? rawValue,
        string? displayName,
        string? canonicalKey,
        AssetKind kind)
    {
        foreach (var candidate in new[] { rawValue, displayName, canonicalKey })
        {
            var value = NormalizeHost(candidate);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (kind == AssetKind.Subdomain)
            {
                return ToParentDomain(value);
            }

            return value;
        }

        return string.Empty;
    }

    private static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim().TrimEnd('.');

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            candidate = uri.Host;
        }
        else
        {
            var slash = candidate.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                candidate = candidate[..slash];
            }

            var colon = candidate.LastIndexOf(':');
            if (colon > -1 && candidate.Count(c => c == ':') == 1)
            {
                candidate = candidate[..colon];
            }
        }

        candidate = candidate.Trim().TrimEnd('.').ToLowerInvariant();

        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[4..];
        }

        return candidate;
    }

    private static string ToParentDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length <= 2
            ? host
            : string.Join('.', parts[^2], parts[^1]);
    }

    private static HashSet<Guid> ParseTargetIds(string[]? targetIds)
    {
        return targetIds?
            .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .ToHashSet() ?? [];
    }

    private static async Task<WorkerEnsureResult> EnsureWorkersAvailableAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct,
        params (string ServiceName, int MinimumCount)[] requiredWorkers)
    {
        try
        {
            var counts = await DockerComposeWorkerScaler
                .GetRunningServiceCountsAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            var scaled = new List<string>();

            foreach (var worker in requiredWorkers)
            {
                counts.TryGetValue(worker.ServiceName, out var running);

                if (running >= worker.MinimumCount)
                {
                    continue;
                }

                await DockerComposeWorkerScaler
                    .ScaleWorkerAsync(worker.ServiceName, worker.MinimumCount, configuration, logger, ct)
                    .ConfigureAwait(false);

                scaled.Add($"{worker.ServiceName}:{running}->{worker.MinimumCount}");
            }

            return scaled.Count == 0
                ? new WorkerEnsureResult(true, "required workers already running")
                : new WorkerEnsureResult(true, $"scaled {string.Join(", ", scaled)}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not verify or scale required workers before queuing work.");
            return new WorkerEnsureResult(false, ex.Message);
        }
    }

    private sealed record FallbackTarget(
        Guid Id,
        string RootDomain,
        int GlobalMaxDepth,
        DateTimeOffset CreatedAtUtc);

    private sealed record WorkerEnsureResult(bool Succeeded, string Message);

    private sealed class ToolRestartEndpointsLogger
    {
    }

    public static void Map(WebApplication app) => app.MapToolRestartEndpoints();
}
