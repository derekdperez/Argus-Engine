using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Gatekeeper.Consumers;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-gatekeeper");
builder.Services.AddArgusInfrastructure(builder.Configuration);
builder.Services.AddScoped<GatekeeperOrchestrator>();

builder.Services.AddArgusRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<AssetDiscoveredConsumer>();
        x.AddConsumer<AssetRelationshipDiscoveredConsumer>();
    });

var host = builder.Build();
var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var configuration = host.Services.GetRequiredService<IConfiguration>();
var environment = host.Services.GetRequiredService<IHostEnvironment>();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

if (!ShouldSkipStartupDatabase(configuration))
{
    await InitializeGatekeeperDatabaseAsync(
        host.Services,
        configuration,
        environment,
        startupLog,
        lifetime.ApplicationStopping)
        .ConfigureAwait(false);
}
else
{
    startupLog.LogInformation("Skipping startup database bootstrap for gatekeeper.");
}

await host.RunAsync().ConfigureAwait(false);

static async Task InitializeGatekeeperDatabaseAsync(
    IServiceProvider services,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger startupLog,
    CancellationToken stoppingToken)
{
    var continueOnFailure = configuration.GetArgusValue(
        "ContinueOnStartupDatabaseFailure",
        environment.IsDevelopment());

    var retryDelays = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
    };

    for (var attempt = 1; attempt <= retryDelays.Length + 1; attempt++)
    {
        try
        {
            await ArgusDbBootstrap.InitializeAsync(
                services,
                configuration,
                startupLog,
                includeFileStore: false,
                stoppingToken)
                .ConfigureAwait(false);

            GatekeeperLogMessages.DatabaseBootstrapCompleted(startupLog);
            return;
        }
        catch (Exception ex) when (attempt <= retryDelays.Length && !stoppingToken.IsCancellationRequested)
        {
            GatekeeperLogMessages.DatabaseBootstrapRetry(startupLog, ex, attempt);

            await Task.Delay(retryDelays[attempt - 1], stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            GatekeeperLogMessages.DatabaseBootstrapFailed(startupLog, ex, continueOnFailure);

            if (!continueOnFailure)
            {
                throw;
            }

            return;
        }
    }
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
