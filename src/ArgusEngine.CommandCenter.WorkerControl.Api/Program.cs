using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet(
    "/health/ready",
    async (ArgusDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct).ConfigureAwait(false)
            ? Results.Ok(new { status = "ready", postgres = "ok" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.MapGet(
    "/api/workers/control/routes",
    () => Results.Ok(
        new
        {
            owner = "command-center-worker-control-api",
            implemented = false,
            legacyDependency = false,
            routes = new[]
            {
                "/api/workers",
                "/api/ec2-workers",
                "/api/ops/ecs-status",
                "/api/ops/spider/restart",
                "/api/ops/subdomain-enum/restart",
            },
        }))
    .WithName("WorkerControlRouteOwnership")
    .AllowAnonymous();

app.MapPost(
    "/api/workers/{worker}/restart",
    (string worker, RestartWorkerRequest request) =>
        Results.Json(
            new RestartWorkerResponse(
                worker,
                "NotImplemented",
                request.CorrelationId ?? Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow),
            statusCode: StatusCodes.Status501NotImplemented))
    .WithName("RestartWorker");

app.Map("/{**path}", (HttpContext context) =>
{
    var path = context.Request.Path;

    if (Owns(path))
    {
        return Results.Json(
            new
            {
                error = "Worker-control route is owned by command-center-worker-control-api but has not been ported from the legacy CommandCenter implementation yet.",
                owner = "command-center-worker-control-api",
                path = path.Value,
                legacyFallback = false,
                nextStep = "Move worker, EC2, ECS, scaling, and restart orchestration logic into this host before treating the refactor as complete.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
    }

    return Results.NotFound(new { error = "Route is not owned by command-center-worker-control-api.", path = path.Value });
});

await app.RunAsync().ConfigureAwait(false);

static bool Owns(PathString path) =>
    path.StartsWithSegments("/api/workers", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/ec2-workers", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/ops/ecs-status", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/ops/spider/restart", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/ops/subdomain-enum/restart", StringComparison.OrdinalIgnoreCase);
