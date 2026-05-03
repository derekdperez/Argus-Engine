using Microsoft.Extensions.Hosting;
using NightmareV2.Application.Gatekeeping;
using NightmareV2.Gatekeeper.Consumers;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Configuration;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Infrastructure.Observability;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-gatekeeper");
builder.Services.AddArgusInfrastructure(builder.Configuration);
builder.Services.AddScoped<GatekeeperOrchestrator>();

builder.Services.AddNightmareRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<AssetDiscoveredConsumer>();
        x.AddConsumer<AssetRelationshipDiscoveredConsumer>();
    });

var host = builder.Build();

var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
if (!ShouldSkipStartupDatabase(host.Services.GetRequiredService<IConfiguration>()))
{
    await StartupDatabaseBootstrap.InitializeAsync(
            host.Services,
            host.Services.GetRequiredService<IConfiguration>(),
            startupLog,
            includeFileStore: false,
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
        .ConfigureAwait(false);
}
else
{
    startupLog.LogInformation("Skipping startup database bootstrap for gatekeeper.");
}

await host.RunAsync().ConfigureAwait(false);

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
