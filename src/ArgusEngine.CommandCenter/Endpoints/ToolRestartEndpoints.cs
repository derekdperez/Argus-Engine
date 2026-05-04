using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Services.Targets;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class ToolRestartEndpoints
{
    public static IEndpointRouteBuilder MapToolRestartEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/api/ops/subdomain-enum/restart",
                async (RestartToolRequest body, ArgusDbContext db, IEventOutbox outbox, IOptions<SubdomainEnumerationOptions> options, CancellationToken ct) =>
                {
                    var targetsQuery = db.Targets.AsNoTracking();
                    if (!body.AllTargets)
                    {
                        if (body.TargetIds is null || body.TargetIds.Length == 0)
                            return Results.BadRequest("targetIds is required unless allTargets is true");

                        var ids = body.TargetIds
                            .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
                            .Where(x => x != Guid.Empty)
                            .ToHashSet();
                        if (ids.Count == 0)
                            return Results.BadRequest("no valid target ids supplied");

                        targetsQuery = targetsQuery.Where(t => ids.Contains(t.Id));
                    }

                    var targets = await targetsQuery.Take(5000).ToListAsync(ct).ConfigureAwait(false);
                    var providers = options.Value.DefaultProviders
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (providers.Length == 0)
                        providers = ["subfinder", "amass"];

                    var queued = 0;
                    foreach (var target in targets)
                    {
                        var correlation = NewId.NextGuid();
                        foreach (var provider in providers)
                        {
                            var eventId = NewId.NextGuid();
                            await outbox.EnqueueAsync(
                                    new SubdomainEnumerationRequested(
                                        target.Id,
                                        target.RootDomain,
                                        provider,
                                        "command-center-manual-restart",
                                        DateTimeOffset.UtcNow,
                                        correlation,
                                        EventId: eventId,
                                        CausationId: correlation,
                                        Producer: "command-center"),
                                    ct)
                                .ConfigureAwait(false);
                            queued++;
                        }
                    }

                    return Results.Ok(new { Targets = targets.Count, JobsQueued = queued });
                })
            .WithName("RestartSubdomainEnumeration");

        app.MapPost(
                "/api/ops/spider/restart",
                async (
                    RestartToolRequest body,
                    ArgusDbContext db,
                    RootSpiderSeedService rootSpiderSeedService,
                    CancellationToken ct) =>
                {
                    var targetsQuery = db.Targets.AsNoTracking();
                    if (!body.AllTargets)
                    {
                        if (body.TargetIds is null || body.TargetIds.Length == 0)
                            return Results.BadRequest("targetIds is required unless allTargets is true");

                        var ids = body.TargetIds
                            .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
                            .Where(x => x != Guid.Empty)
                            .ToHashSet();
                        if (ids.Count == 0)
                            return Results.BadRequest("no valid target ids supplied");

                        targetsQuery = targetsQuery.Where(t => ids.Contains(t.Id));
                    }

                    var targets = await targetsQuery.Take(5000).ToListAsync(ct).ConfigureAwait(false);
                    var targetIds = targets.Select(t => t.Id).ToHashSet();
                    var now = DateTimeOffset.UtcNow;

                    var existingQueueRows = await db.HttpRequestQueue
                        .Where(q => targetIds.Contains(q.TargetId))
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
                    return Results.Ok(new { Targets = targets.Count, RequeuedExistingRequests = existingQueueRows.Count, RootSeedsQueued = queuedRootSeeds });
                })
            .WithName("RestartSpidering");

        return app;
    }

    public static void Map(WebApplication app) => app.MapToolRestartEndpoints();
}
