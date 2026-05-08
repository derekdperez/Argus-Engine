var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
app.MapHub<DiscoveryHub>("/hubs/discovery");

await app.RunAsync().ConfigureAwait(false);

internal sealed class DiscoveryHub : Microsoft.AspNetCore.SignalR.Hub;
