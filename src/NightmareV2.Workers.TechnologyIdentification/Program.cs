using NightmareV2.Application.TechnologyIdentification;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Workers.TechnologyIdentification;
using NightmareV2.Workers.TechnologyIdentification.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNightmareInfrastructure(builder.Configuration);
builder.Services.AddSingleton<HtmlSignalExtractor>();
builder.Services.AddSingleton<CookieExtractor>();
builder.Services.AddSingleton(
    sp =>
    {
        var logger = sp.GetRequiredService<ILogger<TechnologyCatalogLoader>>();
        var catalogRoot = Path.Combine(AppContext.BaseDirectory, "Resources", "TechnologyDetection");
        return new TechnologyCatalogLoader(logger).Load(catalogRoot);
    });
builder.Services.AddSingleton<TechnologyScanner>();
builder.Services.AddNightmareRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<TechnologyIdentificationConsumer>();
    });

var host = builder.Build();
var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
await StartupDatabaseBootstrap.InitializeAsync(
        host.Services,
        host.Services.GetRequiredService<IConfiguration>(),
        startupLog,
        includeFileStore: false,
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
    .ConfigureAwait(false);


try
{
    await SeedTechnologyTagsAsync(host).ConfigureAwait(false);
}
catch (Exception ex)
{
    startupLog.LogWarning(ex, "Technology tag catalog seed failed during startup; missing tags will still be created idempotently at match time.");
}


await host.RunAsync().ConfigureAwait(false);

static async Task SeedTechnologyTagsAsync(IHost host)
{
    await using var scope = host.Services.CreateAsyncScope();
    var catalog = scope.ServiceProvider.GetRequiredService<TechnologyCatalog>();
    var tagService = scope.ServiceProvider.GetRequiredService<IAssetTagService>();
    var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
    await tagService.SeedTechnologyTagsAsync(catalog.Technologies.Values.ToArray(), lifetime.ApplicationStopping)
        .ConfigureAwait(false);
}
