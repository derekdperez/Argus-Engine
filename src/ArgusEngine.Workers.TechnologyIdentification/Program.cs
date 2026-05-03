using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.TechnologyIdentification;
using ArgusEngine.Workers.TechnologyIdentification.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-techid");
builder.Services.AddArgusInfrastructure(builder.Configuration);

builder.Services.AddOptions<TechnologyIdentificationScanOptions>()
    .Bind(builder.Configuration.GetSection("Argus:TechnologyIdentification"))
    .ValidateOnStart();

builder.Services.AddSingleton<TechnologyScanner>();
builder.Services.AddSingleton<HtmlSignalExtractor>();
builder.Services.AddSingleton<CookieExtractor>();
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<TechnologyCatalogLoader>>();
    var catalogRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "TechnologyDetection");
    return new TechnologyCatalogLoader(logger).Load(catalogRoot);
});

builder.Services.AddArgusRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<TechnologyIdentificationConsumer>();
    });

var host = builder.Build();

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

try
{
    await SeedTechnologyTagsAsync(host).ConfigureAwait(false);
}
catch (Exception ex)
{
    startupLog.LogWarning(ex, "Technology tag catalog seed failed during startup; missing tags will still be created idempotently at match time.");
}

await host.RunAsync().ConfigureAwait(false);

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);

static async Task SeedTechnologyTagsAsync(IHost host)
{
    await using var scope = host.Services.CreateAsyncScope();
    var catalog = scope.ServiceProvider.GetRequiredService<TechnologyCatalog>();
    var tagService = scope.ServiceProvider.GetRequiredService<IAssetTagService>();
    var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();

    await tagService.SeedTechnologyTagsAsync(catalog.Technologies.Values.ToArray(), lifetime.ApplicationStopping)
        .ConfigureAwait(false);
}
