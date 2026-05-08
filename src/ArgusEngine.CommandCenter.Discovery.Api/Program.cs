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
    "/api/discovery/routes",
    () => Results.Ok(
        new
        {
            owner = "command-center-discovery-api",
            implemented = false,
            legacyDependency = false,
            routes = new[]
            {
                "/api/targets",
                "/api/assets",
                "/api/asset-graph",
                "/api/tags",
                "/api/technologies",
                "/api/asset-admission-decisions",
                "/api/high-value-findings",
                "/api/technology-identification",
                "/api/http-request-queue",
                "/api/filestore",
                "/api/events",
            },
        }))
    .WithName("DiscoveryApiRouteOwnership")
    .AllowAnonymous();

app.Map("/{**path}", (HttpContext context) =>
{
    var path = context.Request.Path;

    if (Owns(path))
    {
        return Results.Json(
            new
            {
                error = "Discovery route is owned by command-center-discovery-api but has not been ported from the legacy CommandCenter implementation yet.",
                owner = "command-center-discovery-api",
                path = path.Value,
                legacyFallback = false,
                nextStep = "Move the legacy endpoint logic into this host, keep the same route contract, and add a gateway parity test.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
    }

    return Results.NotFound(new { error = "Route is not owned by command-center-discovery-api.", path = path.Value });
});

await app.RunAsync().ConfigureAwait(false);

static bool Owns(PathString path) =>
    path.StartsWithSegments("/api/targets", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/assets", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/asset-graph", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/tags", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/technologies", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/asset-admission-decisions", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/high-value-findings", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/technology-identification", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/http-request-queue", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/filestore", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/discovery", StringComparison.OrdinalIgnoreCase);
