using ArgusEngine.Application.Workers;
using ArgusEngine.Workers.PortScan;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.PortScan.Consumers;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-portscan");
    builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
    builder.Services.AddArgusWorkerHeartbeat(ArgusEngine.Application.Workers.WorkerKeys.PortScan);
    builder.Services.AddScoped<IWorkerHealthCheck, PortScanWorkerHealthCheck>();

    builder.Services.AddArgusRabbitMq(
        builder.Configuration,
        x =>
        {
            x.AddConsumer<PortScanRequestedConsumer>();
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
        startupLog.LogInformation("Skipping startup database bootstrap for port scan worker.");
    }
#pragma warning restore CA1848

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: PortScan worker failed to start. {ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
