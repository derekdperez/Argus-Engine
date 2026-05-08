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
                routes = new[]
                {
                    "/api/targets",
                    "/api/assets",
                    "/api/tags",
                    "/api/http-request-queue",
                    "/api/high-value-findings",
                    "/api/technology-identification",
                },
            }))
    .WithName("DiscoveryApiRouteOwnership");

await app.RunAsync().ConfigureAwait(false);
