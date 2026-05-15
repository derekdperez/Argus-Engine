using System.Net;
using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.HttpRequester;
using ArgusEngine.Workers.Spider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.AddFilter("System.Net.Http.HttpClient.requester", LogLevel.Warning);
    builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-http-requester");
    builder.Services.AddArgusDatabaseLogging("worker-http-requester");

    builder.Services
        .AddOptions<HttpRequesterOptions>()
        .Bind(builder.Configuration.GetSection("HttpRequester"))
        .Validate(options => options.MaxConcurrency > 0, "HttpRequester:MaxConcurrency must be greater than zero.")
        .Validate(options => options.VisibilityTimeoutSeconds > 0, "HttpRequester:VisibilityTimeoutSeconds must be greater than zero.")
        .Validate(options => options.PollIntervalSeconds > 0, "HttpRequester:PollIntervalSeconds must be greater than zero.")
        .ValidateOnStart();

    builder.Services.AddSingleton<AdaptiveConcurrencyController>();
    builder.Services.AddSingleton<ProxyHttpClientProvider>();
    builder.Services.AddHostedService<HttpRequesterWorker>();

    builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
    builder.Services.AddArgusWorkerHeartbeat(WorkerKeys.HttpRequester);
    builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });

    builder.Services
        .AddHttpClient("requester")
        .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<HttpRequesterOptions>>().Value;

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.All,
                CheckCertificateRevocationList = false
            };

            if (options.AllowInsecureSsl)
            {
#pragma warning disable CA5359
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#pragma warning restore CA5359
            }

            return handler;
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
#pragma warning restore CA1848

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CRITICAL: HTTP Requester worker failed to start.{Environment.NewLine}{ex}");
    throw;
}

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false) ||
    string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
