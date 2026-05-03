using Microsoft.Extensions.Hosting;
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

builder.Services.AddNightmareRabbitMq(
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
            await StartupDatabaseBootstrap.InitializeAsync(
                services,
                configuration,
                startupLog,
                includeFileStore: false,
                stoppingToken)
                .ConfigureAwait(false);

            startupLog.LogInformation("Gatekeeper startup database bootstrap completed.");
            return;
        }
        catch (Exception ex) when (attempt <= retryDelays.Length && !stoppingToken.IsCancellationRequested)
        {
            startupLog.LogWarning(
                ex,
                "Gatekeeper startup database bootstrap failed on attempt {Attempt}; retrying.",
                attempt);

            await Task.Delay(retryDelays[attempt - 1], stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            startupLog.LogCritical(
                ex,
                "Gatekeeper startup database bootstrap failed. ContinueOnStartupDatabaseFailure={ContinueOnStartupDatabaseFailure}.",
                continueOnFailure);

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
