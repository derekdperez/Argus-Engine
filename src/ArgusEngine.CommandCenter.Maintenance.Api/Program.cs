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
    "/api/maintenance/routes",
    () => Results.Ok(
        new
        {
            owner = "command-center-maintenance-api",
            implemented = false,
            legacyDependency = false,
            routes = new[]
            {
                "/api/maintenance",
                "/api/admin/data-retention",
                "/api/admin/partitions",
                "/api/admin/usage",
                "/api/diagnostics",
                "/api/bus",
            },
        }))
    .WithName("MaintenanceRouteOwnership")
    .AllowAnonymous();

app.Map("/{**path}", (HttpContext context) =>
{
    var path = context.Request.Path;

    if (Owns(path))
    {
        return Results.Json(
            new
            {
                error = "Maintenance/admin route is owned by command-center-maintenance-api but has not been ported from the legacy CommandCenter implementation yet.",
                owner = "command-center-maintenance-api",
                path = path.Value,
                legacyFallback = false,
                nextStep = "Move data-retention, partition, artifact-backfill, diagnostics, usage, and bus journal logic into this host.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
    }

    return Results.NotFound(new { error = "Route is not owned by command-center-maintenance-api.", path = path.Value });
});

await app.RunAsync().ConfigureAwait(false);

static bool Owns(PathString path) =>
    path.StartsWithSegments("/api/maintenance", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/diagnostics", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/bus", StringComparison.OrdinalIgnoreCase);
