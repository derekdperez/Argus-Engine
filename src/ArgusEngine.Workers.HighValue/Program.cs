using System.Text.Json;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.HighValue;
using ArgusEngine.Workers.HighValue.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-highvalue");

builder.Services.AddTransient<WorkerCorrelationHandler>();
builder.Services.AddHttpClient(string.Empty)
    .AddHttpMessageHandler<WorkerCorrelationHandler>();

builder.Services.AddArgusInfrastructure(builder.Configuration);

var patternPath = Path.Combine(AppContext.BaseDirectory, "Resources", "RegexPatterns", "high_value_targets.txt");
var definitions = HighValuePatternCatalog.LoadFromFile(patternPath);
builder.Services.AddSingleton(new HighValueRegexMatcher(definitions));

var wordlistDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Wordlists", "high_value");
builder.Services.AddSingleton(new HighValueWordlistBootstrap(HighValueWordlistCatalog.LoadFromDirectory(wordlistDir)));

builder.Services.AddNightmareRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<HighValueRegexConsumer>();
        x.AddConsumer<HighValuePathGuessConsumer>();
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
    startupLog.LogInformation("Skipping startup database bootstrap for high-value worker.");
}

await host.RunAsync().ConfigureAwait(false);

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
