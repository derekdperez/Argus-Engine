using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.Spider;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-spider");
    builder.Services.AddArgusDatabaseLogging("worker-spider");

    builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: true);
    builder.Services.AddArgusWorkerHeartbeat(ArgusEngine.Application.Workers.WorkerKeys.Spider);
    builder.Services.AddScoped<IWorkerHealthCheck, SpiderWorkerHealthCheck>();
    builder.Services.AddHttpClient();

    builder.Services.AddArgusRabbitMq(builder.Configuration, bus =>
    {
        bus.AddConsumer<HttpResponseDownloadedConsumer>();
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
            includeFileStore: true,
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
            .ConfigureAwait(false);
    }
#pragma warning restore CA1848

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: Spider worker failed to start. {ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
