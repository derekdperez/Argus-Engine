using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.Spider;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-spider");

builder.Services.AddSingleton<AdaptiveConcurrencyController>();
builder.Services.AddHostedService<HttpRequestQueueWorker>();

builder.Services.AddArgusInfrastructure(builder.Configuration);
builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });

builder.Services.AddHttpClient("spider")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.All,
        CheckCertificateRevocationList = false,
#pragma warning disable CA5359 // Insecure SSL is intentional for wide-range reconnaissance
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
#pragma warning restore CA5359
    })
    .AddPolicyHandler(HttpRetryPolicies.SpiderRetryPolicy());

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
else
{
    startupLog.LogInformation("Skipping startup database bootstrap for spider worker.");
}
#pragma warning restore CA1848

await host.RunAsync().ConfigureAwait(false);

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
