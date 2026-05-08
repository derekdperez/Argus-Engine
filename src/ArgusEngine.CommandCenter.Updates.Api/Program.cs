var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();

app.MapGet(
    "/api/development/components",
    () => Results.Json(
        new
        {
            owner = "command-center-updates-api",
            implemented = false,
            legacyDependency = false,
            components = Array.Empty<object>(),
            message = "Component update listing is owned by the split Updates API but still needs to be ported from the legacy CommandCenter implementation.",
        },
        statusCode: StatusCodes.Status501NotImplemented))
    .WithName("ListComponentUpdates");

app.Map("/{**path}", (HttpContext context) =>
{
    var path = context.Request.Path;

    if (path.StartsWithSegments("/api/development/components", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new
            {
                error = "Component update route is owned by command-center-updates-api but has not been ported from the legacy CommandCenter implementation yet.",
                owner = "command-center-updates-api",
                path = path.Value,
                legacyFallback = false,
                nextStep = "Move component list, log, and update execution logic into this host and retain production safety controls.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
    }

    return Results.NotFound(new { error = "Route is not owned by command-center-updates-api.", path = path.Value });
});

await app.RunAsync().ConfigureAwait(false);
