using ArgusEngine.CommandCenter.Discovery.Api.Endpoints;
using ArgusEngine.CommandCenter.Discovery.Api.Services;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });
builder.Services.AddScoped<RootSpiderSeedService>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet(
    "/health/ready",
    async (ArgusDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct).ConfigureAwait(false)
            ? Results.Ok(new { status = "ready", postgres = "ok" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.MapAssetAdmissionDecisionEndpoints();
app.MapAssetEndpoints();
app.MapAssetGraphEndpoints();
app.MapEventTraceEndpoints();
app.MapFileStoreEndpoints();
app.MapHighValueFindingEndpoints();
app.MapHttpRequestQueueEndpoints();
app.MapTagEndpoints();
app.MapTargetEndpoints();
app.MapTechnologyIdentificationEndpoints();

await app.RunAsync().ConfigureAwait(false);
