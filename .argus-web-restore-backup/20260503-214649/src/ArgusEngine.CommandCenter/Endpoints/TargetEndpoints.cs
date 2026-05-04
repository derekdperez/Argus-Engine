using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using ArgusEngine.Application.Events;
using ArgusEngine.CommandCenter.Hubs;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Services.Targets;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class TargetEndpoints
{
    public static IEndpointRouteBuilder MapTargetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/targets",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var list = await db.Targets.AsNoTracking()
                        .OrderBy(t => t.RootDomain)
                        .Select(
                            t => new TargetSummary(
                                t.Id,
                                t.RootDomain,
                                t.GlobalMaxDepth,
                                t.CreatedAtUtc,
                                0, 0, 0, 0,
                                null))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(list);
                })
            .WithName("GetTargets");

        app.MapPost(
                "/api/targets",
                async (
                    CreateTargetRequest request,
                    ArgusDbContext db,
                    IEventOutbox outbox,
                    RootSpiderSeedService rootSpiderSeedService,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    if (!TargetRootNormalization.TryNormalize(request.RootDomain, out var normalized))
                        return Results.BadRequest("Invalid root domain format");

                    var exists = await db.Targets.AsNoTracking().AnyAsync(t => t.RootDomain == normalized, ct).ConfigureAwait(false);
                    if (exists)
                        return Results.Conflict("Target already exists");

                    var target = new ReconTarget
                    {
                        Id = Guid.NewGuid(),
                        RootDomain = normalized,
                        GlobalMaxDepth = request.GlobalMaxDepth > 0 ? request.GlobalMaxDepth : 12,
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
                            ct)
                        .ConfigureAwait(false);

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

                    await hub.Clients.All.SendAsync(
                            DiscoveryHubEvents.DomainEvent,
                            new LiveUiEventDto(
                                "TargetCreated",
                                target.Id,
                                target.Id,
                                "targets",
                                $"Target queued: {target.RootDomain}",
                                target.CreatedAtUtc),
                            cancellationToken: ct)
                        .ConfigureAwait(false);

                    return Results.Created($"/api/targets/{target.Id}", target);
                })
            .WithName("CreateTarget");

        return app;
    }

    public static void Map(WebApplication app) => app.MapTargetEndpoints();
}
