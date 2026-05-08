using ArgusEngine.CommandCenter.Updates.Api.Endpoints;
using ArgusEngine.CommandCenter.Updates.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddComponentUpdateServices(builder.Configuration);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();

app.MapComponentUpdateEndpoints();

await app.RunAsync().ConfigureAwait(false);
