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
builder.Services.AddSingleton<GcpCloudRunClient>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<RootSpiderSeedService>();
builder.Services.AddOptions<CoverageAutomationOptions>()
    .Bind(builder.Configuration.GetSection("Argus:CoverageAutomation"))
    .Validate(o => o.InitialDelaySeconds is >= 0 and <= 3600)
    .Validate(o => o.IntervalSeconds is >= 5 and <= 3600)
    .Validate(o => o.EnumerationBatchSize is >= 1 and <= 10_000)
    .Validate(o => o.SpiderBatchSize is >= 1 and <= 20_000)
    .Validate(o => o.EnumerationRetryMinutes is >= 1 and <= 10080)
    .ValidateOnStart();

var autoscalerEnabled = builder.Configuration.GetValue("Argus:Autoscaler:Enabled", defaultValue: true);
if (autoscalerEnabled)
{
    builder.Services.AddHostedService<WorkerAutoscalerBackgroundService>();
}

var coverageAutomationEnabled = builder.Configuration.GetValue("Argus:CoverageAutomation:Enabled", defaultValue: true);
if (coverageAutomationEnabled)
{
    builder.Services.AddHostedService<CoverageAutomationBackgroundService>();
}

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
app.MapWorkerEndpoints();
app.MapDockerWorkerEndpoints();
app.MapGcpWorkerEndpoints();
app.MapRabbitMqStatusEndpoints();

await app.RunAsync().ConfigureAwait(false);
