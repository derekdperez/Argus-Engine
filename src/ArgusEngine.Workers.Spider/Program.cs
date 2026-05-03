using System.Net.Http;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.Spider;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusObservability(builder.Configuration, "argus-worker-spider");

builder.Services.AddOptions<SpiderHttpOptions>()
    .Bind(builder.Configuration.GetSection("Spider:Http"))
    .ValidateOnStart();

var configuredAllowInsecureSpiderSsl = builder.Configuration.GetValue("Spider:Http:AllowInsecureSsl", false);
var allowInsecureSpiderSsl = configuredAllowInsecureSpiderSsl && builder.Environment.IsDevelopment();

if (configuredAllowInsecureSpiderSsl && !builder.Environment.IsDevelopment())
{
    Console.WriteLine("""
        Spider: Spider:Http:AllowInsecureSsl=true was ignored because TLS bypass is allowed only in Development.
        Set DOTNET_ENVIRONMENT=Development for local scanners or set Spider:Http:AllowInsecureSsl=false for deployed environments.
        """);
}
else if (allowInsecureSpiderSsl)
{
    Console.WriteLine("Spider: Spider:Http:AllowInsecureSsl=true — TLS server certificate validation is disabled for HTTP fetches.");
}

builder.Services.AddTransient<SpiderCorrelationHandler>();
builder.Services.AddHttpClient("spider")
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        };

        if (allowInsecureSpiderSsl)
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        return handler;
    })
    .AddHttpMessageHandler<SpiderCorrelationHandler>()
    .AddPolicyHandler(HttpRetryPolicies.SpiderRetryPolicy());

builder.Services.AddArgusInfrastructure(builder.Configuration);
builder.Services.AddHostedService<HttpRequestQueueWorker>();
builder.Services.AddNightmareRabbitMq(builder.Configuration, _ => { });

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
    startupLog.LogInformation("Skipping startup database bootstrap for spider worker.");
}

await host.RunAsync().ConfigureAwait(false);

static bool ShouldSkipStartupDatabase(IConfiguration configuration) =>
    configuration.GetArgusValue("SkipStartupDatabase", false)
    || string.Equals(Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"), "1", StringComparison.OrdinalIgnoreCase);
