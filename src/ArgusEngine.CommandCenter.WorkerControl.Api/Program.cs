using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;
using ArgusEngine.CommandCenter.WorkerControl.Api.Services;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });
builder.Services.AddSingleton<WorkerScaleDefinitionProvider>();
builder.Services.AddSingleton<AwsRegionResolver>();
builder.Services.AddSingleton<EcsServiceNameResolver>();
builder.Services.AddSingleton<EcsWorkerServiceManager>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet(
        "/health/ready",
        async (ArgusDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct).ConfigureAwait(false)
                ? Results.Ok(new { status = "ready", postgres = "ok" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.MapEc2WorkerEndpoints();
app.MapToolRestartEndpoints();
app.MapDockerWorkerEndpoints();
app.MapWorkerEndpoints();

await app.RunAsync().ConfigureAwait(false);