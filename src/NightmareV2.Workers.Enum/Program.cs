using Microsoft.Extensions.Options;
using NightmareV2.Application.Workers;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Configuration;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Infrastructure.Observability;
using NightmareV2.Workers.Enum.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-enum");
builder.Services.AddArgusInfrastructure(builder.Configuration);

builder.Services.AddNightmareRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<TargetCreatedConsumer>();
        x.AddConsumer<SubdomainEnumerationRequestedConsumer>();
    });

var host = builder.Build();

var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var options = host.Services.GetRequiredService<IOptions<SubdomainEnumerationOptions>>().Value;

var subfinderFound = IsToolAvailable(options.Subfinder.BinaryPath);
var amassFound = IsToolAvailable(options.Amass.BinaryPath);
var resolvedWordlistPath = Path.IsPathRooted(options.Amass.WordlistPath)
    ? options.Amass.WordlistPath
    : Path.Combine(AppContext.BaseDirectory, options.Amass.WordlistPath);
var wordlistFound = File.Exists(resolvedWordlistPath);

startupLog.LogInformation(
    "Enumeration tooling probe: subfinder binary found={SubfinderFound}, amass binary found={AmassFound}, wordlist found={WordlistFound}, wordlist path={WordlistPath}",
    subfinderFound,
    amassFound,
    wordlistFound,
    resolvedWordlistPath);

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
    startupLog.LogInformation("Skipping startup database bootstrap for enum worker.");
}

await host.RunAsync().ConfigureAwait(false);

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);

static bool IsToolAvailable(string binaryPath)
{
    if (Path.IsPathRooted(binaryPath))
        return File.Exists(binaryPath);

    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
        return false;

    foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        try
        {
            var full = Path.Combine(dir, binaryPath);
            if (File.Exists(full))
                return true;
        }
        catch
        {
            // Ignore malformed PATH entries.
        }
    }

    return false;
}
