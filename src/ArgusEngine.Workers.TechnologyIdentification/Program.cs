using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Workers;
using ArgusEngine.Workers.TechnologyIdentification;
using Microsoft.Extensions.Configuration;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.TechnologyIdentification;
using ArgusEngine.Workers.TechnologyIdentification.Consumers;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-tech-id");
    builder.Services.AddArgusInfrastructure(builder.Configuration);
    builder.Services.AddArgusWorkerHeartbeat(ArgusEngine.Application.Workers.WorkerKeys.TechnologyIdentification);
    builder.Services.AddScoped<IWorkerHealthCheck, TechIdWorkerHealthCheck>();

    builder.Services.AddSingleton<TechnologyCatalog>(sp =>
    {
        var loader = new TechnologyCatalogLoader(sp.GetRequiredService<ILogger<TechnologyCatalogLoader>>());
        var config = sp.GetRequiredService<IConfiguration>();
        var root = config.GetArgusValue("TechnologyDetection:RootPath") ?? "/app/src/Resources/TechnologyDetection";
        return loader.Load(root);
    });

    builder.Services.AddSingleton<TechnologyScanner>();
    builder.Services.AddSingleton<HtmlSignalExtractor>();
    builder.Services.AddSingleton<CookieExtractor>();

    builder.Services.AddArgusRabbitMq(
        builder.Configuration,
        x =>
        {
            x.AddConsumer<TechnologyIdentificationConsumer>();
        });

    var host = builder.Build();

#pragma warning disable CA1848
    var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    if (!ShouldSkipStartupDatabase(host.Services.GetRequiredService<IConfiguration>()))
    {
        await ArgusDbBootstrap.InitializeAsync(
                host.Services,
                host.Services.GetRequiredService<IConfiguration>(),
                startupLog,
                includeFileStore: false,
                host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
            .ConfigureAwait(false);
    }
    else
    {
        startupLog.LogInformation("Skipping startup database bootstrap for technology identification worker.");
    }
#pragma warning restore CA1848

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: TechnologyIdentification worker failed to start. {ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
