using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Discovery.Api.Services;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Application.Events;

using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class TargetEndpoints
{
    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/targets",
            async (ArgusDbContext db, CancellationToken ct) =>
            {
                var targets = await db.Targets.AsNoTracking()
                    .OrderByDescending(t => t.CreatedAtUtc)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                if (targets.Count == 0)
                {
                    var fallbackRows = await db.Assets.AsNoTracking()
                        .GroupBy(a => a.TargetId)
                        .Select(g => new
                        {
                            TargetId = g.Key,
                            RootDomain = g.Min(a => a.RawValue) ?? g.Key.ToString(),
                            CreatedAtUtc = g.Min(a => a.DiscoveredAtUtc),
                            ConfirmedSubdomains = g.LongCount(a => a.Kind == AssetKind.Subdomain
                                && a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                            ConfirmedAssets = g.LongCount(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                            ConfirmedUrls = g.LongCount(a => a.Kind == AssetKind.Url
                                && a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                            LastAssetAtUtc = g.Max(a => (DateTimeOffset?)a.DiscoveredAtUtc),
                        })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(
                        fallbackRows.Select(
                            row => new TargetSummary(
                                row.TargetId,
                                string.IsNullOrWhiteSpace(row.RootDomain) ? row.TargetId.ToString() : row.RootDomain,
                                12,
                                row.CreatedAtUtc,
                                row.ConfirmedSubdomains,
                                row.ConfirmedAssets,
                                row.ConfirmedUrls,
                                0,
                                row.LastAssetAtUtc)));
                }

                var targetIds = targets.Select(t => t.Id).ToList();
                var now = DateTimeOffset.UtcNow;

                var assetRollups = await db.Assets.AsNoTracking()
                    .Where(a => targetIds.Contains(a.TargetId))
                    .GroupBy(a => a.TargetId)
                    .Select(
                        g => new
                        {
                            TargetId = g.Key,
                            ConfirmedSubdomains = g.LongCount(a => a.Kind == AssetKind.Subdomain
                                && a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                            ConfirmedAssets = g.LongCount(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                            ConfirmedUrls = g.LongCount(a => a.Kind == AssetKind.Url
                                && a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                            LastAssetAtUtc = g.Max(a => (DateTimeOffset?)a.DiscoveredAtUtc),
                        })
                    .ToDictionaryAsync(x => x.TargetId, ct)
                    .ConfigureAwait(false);

                var queueRollups = await db.HttpRequestQueue.AsNoTracking()
                    .Where(q => targetIds.Contains(q.TargetId))
                    .GroupBy(q => q.TargetId)
                    .Select(
                        g => new
                        {
                            TargetId = g.Key,
                            Queued = g.LongCount(q => q.State == HttpRequestQueueState.Queued
                                || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now)),
                            LastQueueAtUtc = g.Max(q => (DateTimeOffset?)q.UpdatedAtUtc),
                        })
                    .ToDictionaryAsync(x => x.TargetId, ct)
                    .ConfigureAwait(false);

                var rows = targets
                    .Select(
                        t =>
                        {
                            assetRollups.TryGetValue(t.Id, out var assets);
                            queueRollups.TryGetValue(t.Id, out var queue);
                            var lastRun = MaxUtc(assets?.LastAssetAtUtc, queue?.LastQueueAtUtc);

                            return new TargetSummary(
                                t.Id,
                                t.RootDomain,
                                t.GlobalMaxDepth,
                                t.CreatedAtUtc,
                                assets?.ConfirmedSubdomains ?? 0,
                                assets?.ConfirmedAssets ?? 0,
                                assets?.ConfirmedUrls ?? 0,
                                queue?.Queued ?? 0,
                                lastRun);
                        })
                    .ToList();

                return Results.Ok(rows);
            })
            .WithName("ListTargets");

        static DateTimeOffset? MaxUtc(DateTimeOffset? first, DateTimeOffset? second)
        {
            if (first is null)
            {
                return second;
            }

            if (second is null)
            {
                return first;
            }

            return first > second ? first : second;
        }

        app.MapPost(
            "/api/targets",
            async (
                CreateTargetRequest dto,
                ArgusDbContext db,
                IEventOutbox outbox,
                RootSpiderSeedService rootSpiderSeedService,
                IPublishEndpoint publishEndpoint,
                CancellationToken ct) =>
            {
                if (!TargetRootNormalization.TryNormalize(dto.RootDomain, out var root))
                {
                    return Results.BadRequest("root domain required");
                }

                var target = new ReconTarget
                {
                    Id = Guid.NewGuid(),
                    RootDomain = root,
                    GlobalMaxDepth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };

                db.Targets.Add(target);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                var correlation = NewId.NextGuid();
                var eventId = NewId.NextGuid();

                await rootSpiderSeedService.QueueRootSpiderSeedsAsync(
                    target.Id,
                    target.RootDomain,
                    target.GlobalMaxDepth,
                    target.CreatedAtUtc,
                    correlation,
                    eventId,
                    ct).ConfigureAwait(false);

                await outbox.EnqueueAsync(
                    new TargetCreated(
                        target.Id,
                        target.RootDomain,
                        target.GlobalMaxDepth,
                        target.CreatedAtUtc,
                        correlation,
                        EventId: eventId,
                        CausationId: correlation,
                        Producer: "command-center"),
                    ct).ConfigureAwait(false);

                await publishEndpoint.Publish(
                    new LiveUiEventDto(
                        "TargetCreated",
                        target.Id,
                        target.Id,
                        "targets",
                        $"Target queued: {target.RootDomain}",
                        target.CreatedAtUtc),
                    cancellationToken: ct).ConfigureAwait(false);

                return Results.Created(
                    $"/api/targets/{target.Id}",
                    new TargetSummary(target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc));
            })
            .WithName("CreateTarget");

        app.MapPut(
            "/api/targets/{id:guid}",
            async (Guid id, UpdateTargetRequest dto, ArgusDbContext db, IPublishEndpoint publishEndpoint, CancellationToken ct) =>
            {
                if (!TargetRootNormalization.TryNormalize(dto.RootDomain, out var root))
                {
                    return Results.BadRequest("root domain required");
                }

                var depth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12;
                var target = await db.Targets.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                if (!string.Equals(target.RootDomain, root, StringComparison.Ordinal))
                {
                    var taken = await db.Targets.AnyAsync(t => t.RootDomain == root && t.Id != id, ct).ConfigureAwait(false);
                    if (taken)
                    {
                        return Results.Conflict("root domain already in use");
                    }
                }

                target.RootDomain = root;
                target.GlobalMaxDepth = depth;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                var summary = new TargetSummary(target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc);

                await publishEndpoint.Publish(
                    new LiveUiEventDto(
                        "TargetUpdated",
                        target.Id,
                        target.Id,
                        "targets",
                        $"Target updated: {target.RootDomain}",
                        DateTimeOffset.UtcNow),
                    cancellationToken: ct).ConfigureAwait(false);

                return Results.Ok(summary);
            })
            .WithName("UpdateTarget");

        app.MapPut(
            "/api/targets/max-depth",
            async (UpdateTargetMaxDepthRequest dto, ArgusDbContext db, IPublishEndpoint publishEndpoint, CancellationToken ct) =>
            {
                if (dto.GlobalMaxDepth <= 0)
                {
                    return Results.BadRequest("globalMaxDepth must be greater than zero");
                }

                IQueryable<ReconTarget> query = db.Targets;
                if (!dto.AllTargets)
                {
                    if (dto.TargetIds is null || dto.TargetIds.Count == 0)
                    {
                        return Results.BadRequest("targetIds is required unless allTargets is true");
                    }

                    var ids = dto.TargetIds.Distinct().ToArray();
                    query = query.Where(t => ids.Contains(t.Id));
                }

                var updated = await query
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(t => t.GlobalMaxDepth, dto.GlobalMaxDepth),
                        ct)
                    .ConfigureAwait(false);

                await publishEndpoint.Publish(
                    new LiveUiEventDto(
                        "TargetsMaxDepthUpdated",
                        null,
                        null,
                        "targets",
                        dto.AllTargets
                            ? $"Max depth set to {dto.GlobalMaxDepth} for all targets"
                            : $"Max depth set to {dto.GlobalMaxDepth} for {updated} targets",
                        DateTimeOffset.UtcNow),
                    cancellationToken: ct).ConfigureAwait(false);

                return Results.Ok(new UpdateTargetMaxDepthResult(updated, dto.GlobalMaxDepth));
            })
            .WithName("UpdateTargetsMaxDepth");

        app.MapDelete(
            "/api/targets/{id:guid}",
            async (Guid id, ArgusDbContext db, IPublishEndpoint publishEndpoint, CancellationToken ct) =>
            {
                var target = await db.Targets.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                var rootDomain = target.RootDomain;
                db.Targets.Remove(target);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                await publishEndpoint.Publish(
                    new LiveUiEventDto(
                        "TargetDeleted",
                        id,
                        id,
                        "targets",
                        $"Target deleted: {rootDomain}",
                        DateTimeOffset.UtcNow),
                    cancellationToken: ct).ConfigureAwait(false);

                return Results.NoContent();
            })
            .WithName("DeleteTarget");

        app.MapPost(
            "/api/targets/bulk",
            async (
                HttpRequest httpRequest,
                ArgusDbContext db,
                IEventOutbox outbox,
                RootSpiderSeedService rootSpiderSeedService,
                IPublishEndpoint publishEndpoint,
                CancellationToken ct) =>
            {
                const int maxLines = 50_000;
                var rawLines = new List<string>();
                var globalDepth = 12;
                var contentType = httpRequest.ContentType ?? "";

                if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    var form = await httpRequest.ReadFormAsync(ct).ConfigureAwait(false);
                    if (form.TryGetValue("globalMaxDepth", out var depthVals)
                        && int.TryParse(depthVals.ToString(), out var parsedDepth)
                        && parsedDepth > 0)
                    {
                        globalDepth = parsedDepth;
                    }

                    var file = form.Files.GetFile("file");
                    if (file is null || file.Length == 0)
                    {
                        return Results.BadRequest("multipart field \"file\" is required");
                    }

                    await using var stream = file.OpenReadStream();
                    using var reader = new StreamReader(stream);
                    var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    rawLines.AddRange(TargetRootNormalization.SplitLines(text));
                }
                else
                {
                    var dto = await httpRequest.ReadFromJsonAsync<BulkImportTargetsRequest>(cancellationToken: ct).ConfigureAwait(false);
                    if (dto is null)
                    {
                        return Results.BadRequest("expected JSON body or multipart/form-data with field \"file\"");
                    }

                    globalDepth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12;
                    if (dto.Domains is not null)
                    {
                        rawLines.AddRange(dto.Domains);
                    }
                }

                if (rawLines.Count > maxLines)
                {
                    return Results.BadRequest($"maximum {maxLines} lines per import");
                }

                var firstOrder = new List<string>();
                var batchSeen = new HashSet<string>(StringComparer.Ordinal);
                var skippedEmpty = 0;
                var skippedDupBatch = 0;
                var skippedInvalid = 0;

                foreach (var line in rawLines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        skippedEmpty++;
                        continue;
                    }

                    if (!TargetRootNormalization.TryNormalize(trimmed, out var normalized))
                    {
                        skippedInvalid++;
                        continue;
                    }

                    if (!batchSeen.Add(normalized))
                    {
                        skippedDupBatch++;
                        continue;
                    }

                    firstOrder.Add(normalized);
                }

                if (firstOrder.Count == 0)
                {
                    return Results.Ok(new BulkImportResult(0, 0, skippedInvalid + skippedEmpty, skippedDupBatch));
                }

                var existing = await db.Targets.AsNoTracking()
                    .Where(t => firstOrder.Contains(t.RootDomain))
                    .Select(t => t.RootDomain)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                var existingSet = existing.ToHashSet(StringComparer.Ordinal);
                var skippedExist = 0;
                var newTargets = new List<ReconTarget>();

                foreach (var normalized in firstOrder)
                {
                    if (existingSet.Contains(normalized))
                    {
                        skippedExist++;
                        continue;
                    }

                    existingSet.Add(normalized);

                    var target = new ReconTarget
                    {
                        Id = Guid.NewGuid(),
                        RootDomain = normalized,
                        GlobalMaxDepth = globalDepth,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                    };

                    newTargets.Add(target);
                    db.Targets.Add(target);
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                foreach (var target in newTargets)
                {
                    var correlation = NewId.NextGuid();
                    var eventId = NewId.NextGuid();

                    await rootSpiderSeedService.QueueRootSpiderSeedsAsync(
                        target.Id,
                        target.RootDomain,
                        target.GlobalMaxDepth,
                        target.CreatedAtUtc,
                        correlation,
                        eventId,
                        ct).ConfigureAwait(false);

                    await outbox.EnqueueAsync(
                        new TargetCreated(
                            target.Id,
                            target.RootDomain,
                            target.GlobalMaxDepth,
                            target.CreatedAtUtc,
                            correlation,
                            EventId: eventId,
                            CausationId: correlation,
                            Producer: "command-center"),
                        ct).ConfigureAwait(false);

                    await publishEndpoint.Publish(
                        new LiveUiEventDto(
                            "TargetCreated",
                            target.Id,
                            target.Id,
                            "targets",
                            $"Target queued: {target.RootDomain}",
                            target.CreatedAtUtc),
                        cancellationToken: ct).ConfigureAwait(false);
                }

                return Results.Ok(
                    new BulkImportResult(
                        newTargets.Count,
                        skippedExist,
                        skippedInvalid + skippedEmpty,
                        skippedDupBatch));
            })
            .WithName("BulkImportTargets");

        return app;
    }

    public static void Map(WebApplication app) => app.MapTargetEndpoints();
}
