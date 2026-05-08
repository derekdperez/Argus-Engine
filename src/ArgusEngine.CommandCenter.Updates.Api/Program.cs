var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
app.MapGet(
        "/api/development/components",
        () => Results.Ok(
            new
            {
                owner = "command-center-updates-api",
                components = Array.Empty<object>(),
            }))
    .WithName("ListComponentUpdates");

await app.RunAsync().ConfigureAwait(false);
