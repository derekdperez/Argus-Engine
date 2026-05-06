using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.Enumeration.Consumers;
using ArgusEngine.Workers.Enumeration;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-enum");
    builder.Services.AddArgusDatabaseLogging("worker-enum");
    builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
    builder.Services.AddArgusWorkerHeartbeat(WorkerKeys.Enumeration);
    builder.Services.AddScoped<IWorkerHealthCheck, EnumWorkerHealthCheck>();

    builder.Services.AddArgusRabbitMq(
        builder.Configuration,
        x =>
        {
            x.AddConsumer<TargetCreatedConsumer>();
            x.AddConsumer<SubdomainEnumerationRequestedConsumer>(typeof(SubdomainEnumerationRequestedConsumerDefinition));
        });

    var host = builder.Build();

    var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var options = host.Services.GetRequiredService<IOptions<SubdomainEnumerationOptions>>().Value;

    var subfinderFound = IsToolAvailable(options.Subfinder.BinaryPath);
    var amassFound = IsToolAvailable(options.Amass.BinaryPath);
    var resolvedWordlistPath = Path.IsPathRooted(options.Amass.WordlistPath)
        ? options.Amass.WordlistPath
        : Path.Combine(AppContext.BaseDirectory, options.Amass.WordlistPath);
    var wordlistFound = File.Exists(resolvedWordlistPath);

    StartupLog.LogToolProbe(startupLogger, subfinderFound, amassFound, wordlistFound, resolvedWordlistPath, null);

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
    else
    {
        StartupLog.LogSkippingBootstrap(startupLogger, null);
    }

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: Enum worker failed to start. {ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);

static bool IsToolAvailable(string binaryPath)
{
    if (string.IsNullOrWhiteSpace(binaryPath))
        return false;
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

static class StartupLog
{
    private static readonly Action<ILogger, bool, bool, bool, string, Exception?> ToolProbe =
        LoggerMessage.Define<bool, bool, bool, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogToolProbe)),
            "Enumeration tooling probe: subfinder binary found={SubfinderFound}, amass binary found={AmassFound}, wordlist found={WordlistFound}, wordlist path={WordlistPath}");

    private static readonly Action<ILogger, Exception?> SkippingBootstrap =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(LogSkippingBootstrap)),
            "Skipping startup database bootstrap for enum worker.");

    public static void LogToolProbe(ILogger logger, bool subfinder, bool amass, bool wordlist, string wordlistPath, Exception? ex) =>
        ToolProbe(logger, subfinder, amass, wordlist, wordlistPath, ex);

    public static void LogSkippingBootstrap(ILogger logger, Exception? ex) =>
        SkippingBootstrap(logger, ex);
}
