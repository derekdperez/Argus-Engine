using ArgusEngine.Application.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Application.Http;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.HighValue;
using ArgusEngine.Workers.HighValue.Consumers;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-highvalue");
    builder.Services.AddArgusInfrastructure(builder.Configuration);
    builder.Services.AddArgusWorkerHeartbeat(ArgusEngine.Application.Workers.WorkerKeys.HighValueRegex);
    builder.Services.AddScoped<IWorkerHealthCheck, HighValueWorkerHealthCheck>();

    builder.Services.AddTransient<WorkerHttpClientHandler>();
    builder.Services.AddHttpClient(string.Empty)
        .AddHttpMessageHandler<WorkerHttpClientHandler>();

    var patternPath = Path.Combine(AppContext.BaseDirectory, "Resources", "RegexPatterns", "high_value_targets.txt");
    var definitions = HighValuePatternCatalog.LoadFromFile(patternPath);
    builder.Services.AddSingleton(new HighValueRegexMatcher(definitions));

    var wordlistDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Wordlists", "high_value");
    builder.Services.AddSingleton(new HighValueWordlistBootstrap(HighValueWordlistCatalog.LoadFromDirectory(wordlistDir)));

    builder.Services.AddArgusRabbitMq(
        builder.Configuration,
        x =>
        {
            x.AddConsumer<HighValueRegexConsumer>();
            x.AddConsumer<HighValuePathGuessConsumer>();
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
        startupLog.LogInformation("Skipping startup database bootstrap for high-value worker.");
    }
#pragma warning restore CA1848

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: HighValue worker failed to start. {ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
