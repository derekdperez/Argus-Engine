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
                routes = new[] { "/api/workers", "/api/ec2-workers", "/api/ops/ecs-status" },
            }))
    .WithName("WorkerControlRouteOwnership");

app.MapPost(
        "/api/workers/{worker}/restart",
        (string worker, RestartWorkerRequest request) =>
            Results.Accepted(
                $"/api/workers/{Uri.EscapeDataString(worker)}",
                new RestartWorkerResponse(
                    worker,
                    "Accepted",
                    request.CorrelationId ?? Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow)))
    .WithName("RestartWorker");

await app.RunAsync().ConfigureAwait(false);
