using System.Text.Json;
using NightmareV2.Application.HighValue;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Configuration;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Infrastructure.Observability;
using NightmareV2.Workers.HighValue;
using NightmareV2.Workers.HighValue.Consumers;

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
