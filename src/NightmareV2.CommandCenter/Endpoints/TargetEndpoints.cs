using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.Events;
using NightmareV2.CommandCenter.Hubs;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class TargetEndpoints
{
    private static StoredAsset CreateRootAsset(ReconTarget target, string discoveredBy)
    {
        var root = target.RootDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        return new StoredAsset
        {
            Id = Guid.NewGuid(),
            TargetId = target.Id,
            Kind = AssetKind.Target,
            Category = AssetCategory.ScopeRoot,
            CanonicalKey = $"target:{root}",
            RawValue = root,
            DisplayName = root,
            Depth = 0,
            DiscoveredBy = discoveredBy,
            DiscoveryContext = "Root target asset",
            DiscoveredAtUtc = target.CreatedAtUtc == default ? now : target.CreatedAtUtc,
            LastSeenAtUtc = now,
            Confidence = 1.0m,
            LifecycleStatus = AssetLifecycleStatus.Confirmed,
        };
    }

    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/targets",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var rows = await db.Targets.AsNoTracking()
                        .OrderByDescending(t => t.CreatedAtUtc)
                        .Select(t => new TargetSummary(t.Id, t.RootDomain, t.GlobalMaxDepth, t.CreatedAtUtc))
                        .Take(5000)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("ListTargets");

        app.MapPost(
                "/api/targets",
                async (
                    CreateTargetRequest dto,
                    NightmareDbContext db,
                    IEventOutbox outbox,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    if (!TargetRootNormalization.TryNormalize(dto.RootDomain, out var root))
                        return Results.BadRequest("root domain required");

                    var target = new ReconTarget
                    {
                        Id = Guid.NewGuid(),
                        RootDomain = root,
                        GlobalMaxDepth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                    };

                    db.Targets.Add(target);
                    db.Assets.Add(CreateRootAsset(target, "command-center"));
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    var correlation = NewId.NextGuid();
                    var eventId = NewId.NextGuid();
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
                            ct)
                        .ConfigureAwait(false);

                    await hub.Clients.All.SendAsync("TargetQueued", target.Id, target.RootDomain, cancellationToken: ct)
                        .ConfigureAwait(false);

                    return Results.Created($"/api/targets/{target.Id}", new TargetSummary(target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc));
                })
            .WithName("CreateTarget");

        app.MapPut(
                "/api/targets/{id:guid}",
                async (Guid id, UpdateTargetRequest dto, NightmareDbContext db, CancellationToken ct) =>
                {
                    if (!TargetRootNormalization.TryNormalize(dto.RootDomain, out var root))
                        return Results.BadRequest("root domain required");

                    var depth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12;
                    var target = await db.Targets.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
                    if (target is null)
                        return Results.NotFound();

                    if (!string.Equals(target.RootDomain, root, StringComparison.Ordinal))
                    {
                        var taken = await db.Targets.AnyAsync(t => t.RootDomain == root && t.Id != id, ct).ConfigureAwait(false);
                        if (taken)
                            return Results.Conflict("root domain already in use");
                    }

                    target.RootDomain = root;
                    target.GlobalMaxDepth = depth;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new TargetSummary(target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc));
                })
            .WithName("UpdateTarget");

        app.MapDelete(
                "/api/targets/{id:guid}",
                async (Guid id, NightmareDbContext db, CancellationToken ct) =>
                {
                    var target = await db.Targets.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
                    if (target is null)
                        return Results.NotFound();
                    db.Targets.Remove(target);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.NoContent();
                })
            .WithName("DeleteTarget");

        app.MapPost(
                "/api/targets/bulk",
                async (HttpRequest httpRequest, NightmareDbContext db, IEventOutbox outbox, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
                {
                    const int maxLines = 50_000;
                    var rawLines = new List<string>();
                    var globalDepth = 12;
                    var contentType = httpRequest.ContentType ?? "";

                    if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                    {
                        var form = await httpRequest.ReadFormAsync(ct).ConfigureAwait(false);
                        if (form.TryGetValue("globalMaxDepth", out var depthVals) && int.TryParse(depthVals.ToString(), out var parsedDepth) && parsedDepth > 0)
                            globalDepth = parsedDepth;
                        var file = form.Files.GetFile("file");
                        if (file is null || file.Length == 0)
                            return Results.BadRequest("multipart field \"file\" is required");
                        await using var stream = file.OpenReadStream();
                        using var reader = new StreamReader(stream);
                        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                        rawLines.AddRange(TargetRootNormalization.SplitLines(text));
                    }
                    else
                    {
                        var dto = await httpRequest.ReadFromJsonAsync<BulkImportRequest>(cancellationToken: ct).ConfigureAwait(false);
                        if (dto is null)
                            return Results.BadRequest("expected JSON body or multipart/form-data with field \"file\"");
                        globalDepth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12;
                        if (dto.Domains is not null)
                            rawLines.AddRange(dto.Domains);
                    }

                    if (rawLines.Count > maxLines)
                        return Results.BadRequest($"maximum {maxLines} lines per import");

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

                        if (!TargetRootNormalization.TryNormalize(trimmed, out var n))
                        {
                            skippedInvalid++;
                            continue;
                        }

                        if (!batchSeen.Add(n))
                        {
                            skippedDupBatch++;
                            continue;
                        }

                        firstOrder.Add(n);
                    }

                    if (firstOrder.Count == 0)
                    {
                        return Results.Ok(
                            new BulkImportResult(
                                0,
                                0,
                                skippedInvalid + skippedEmpty,
                                skippedDupBatch));
                    }

                    var existing = await db.Targets.AsNoTracking()
                        .Where(t => firstOrder.Contains(t.RootDomain))
                        .Select(t => t.RootDomain)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var existingSet = existing.ToHashSet(StringComparer.Ordinal);

                    var skippedExist = 0;
                    var newTargets = new List<ReconTarget>();
                    foreach (var n in firstOrder)
                    {
                        if (existingSet.Contains(n))
                        {
                            skippedExist++;
                            continue;
                        }

                        existingSet.Add(n);
                        var target = new ReconTarget
                        {
                            Id = Guid.NewGuid(),
                            RootDomain = n,
                            GlobalMaxDepth = globalDepth,
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                        };
                        newTargets.Add(target);
                        db.Targets.Add(target);
                        db.Assets.Add(CreateRootAsset(target, "command-center-bulk"));
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    foreach (var target in newTargets)
                    {
                        var correlation = NewId.NextGuid();
                        var eventId = NewId.NextGuid();
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
                                ct)
                            .ConfigureAwait(false);
                        await hub.Clients.All.SendAsync("TargetQueued", target.Id, target.RootDomain, cancellationToken: ct)
                            .ConfigureAwait(false);
                    }

                    return Results.Ok(
                        new BulkImportResult(
                            newTargets.Count,
                            skippedExist,
                            skippedInvalid + skippedEmpty,
                            skippedDupBatch));
                })
            .WithName("BulkImportTargets");
    }
}
