using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.Orchestration.Configuration;
using ArgusEngine.Workers.Orchestration.Persistence;
using ArgusEngine.Workers.Orchestration.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.Configure<ReconOrchestratorOptions>(
        builder.Configuration.GetSection(ReconOrchestratorOptions.SectionName));

    builder.Services.AddSingleton<IReconOrchestratorRepository, PostgresReconOrchestratorRepository>();
    builder.Services.AddSingleton<IReconProfilePlanner, ReconProfilePlanner>();
    builder.Services.AddHostedService<ReconOrchestratorHostedService>();

    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-recon-orchestrator");
    builder.Services.AddArgusDatabaseLogging("worker-recon-orchestrator");
    builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
    builder.Services.AddArgusWorkerHeartbeat(WorkerKeys.ReconOrchestrator);

    // The orchestrator currently publishes existing contracts and does not consume a queue.
    builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });

    var host = builder.Build();

    var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var options = host.Services.GetRequiredService<IOptions<ReconOrchestratorOptions>>().Value;

    if (options.ApplySchemaOnStartup)
    {
        await host.Services.GetRequiredService<IReconOrchestratorRepository>()
            .EnsureSchemaAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
            .ConfigureAwait(false);
    }

    if (!ShouldSkipStartupDatabase(host.Services.GetRequiredService<IConfiguration>()))
    {
        await ArgusDbBootstrap.InitializeAsync(
                host.Services,
                host.Services.GetRequiredService<IConfiguration>(),
                startupLogger,
                includeFileStore: false,
                host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
            .ConfigureAwait(false);
    }

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: Recon orchestrator failed to start. {ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
