using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddSingleton<HttpQueueArtifactBackfillService>();

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
        "/api/maintenance/routes",
        () => Results.Ok(
            new
            {
                owner = "command-center-maintenance-api",
                routes = new[]
                {
                    "/api/maintenance/status",
                    "/api/maintenance/backfill-http-artifacts",
                    "/api/admin/data-retention",
                    "/api/admin/partitions",
                    "/api/diagnostics",
                },
            }))
    .WithName("MaintenanceRouteOwnership");

await app.RunAsync().ConfigureAwait(false);
