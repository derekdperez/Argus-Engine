using System.Net.Http;
using System.Net.Security;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Workers.Spider;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<SpiderHttpOptions>()
    .Bind(builder.Configuration.GetSection("Spider:Http"))
    .ValidateOnStart();

var configuredAllowInsecureSpiderSsl = builder.Configuration.GetValue("Spider:Http:AllowInsecureSsl", false);
var allowInsecureSpiderSsl = configuredAllowInsecureSpiderSsl && builder.Environment.IsDevelopment();
if (configuredAllowInsecureSpiderSsl && !builder.Environment.IsDevelopment())
{
    Console.WriteLine("Spider: Spider:Http:AllowInsecureSsl=true was ignored because TLS bypass is allowed only in Development. Set DOTNET_ENVIRONMENT=Development for local scanners or set Spider:Http:AllowInsecureSsl=false for deployed environments.");
}
else if (allowInsecureSpiderSsl)
{
    Console.WriteLine("Spider: Spider:Http:AllowInsecureSsl=true — TLS server certificate validation is disabled for HTTP fetches.");
}

builder.Services.AddHttpClient("spider")
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        };
        if (allowInsecureSpiderSsl)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    })
    .AddPolicyHandler(HttpRetryPolicies.SpiderRetryPolicy());

builder.Services.AddNightmareInfrastructure(builder.Configuration);
builder.Services.AddHostedService<HttpRequestQueueWorker>();
builder.Services.AddNightmareRabbitMq(builder.Configuration, _ => { });

var host = builder.Build();
var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
await StartupDatabaseBootstrap.InitializeAsync(
        host.Services,
        host.Services.GetRequiredService<IConfiguration>(),
        startupLog,
        includeFileStore: false,
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping)
    .ConfigureAwait(false);

await host.RunAsync().ConfigureAwait(false);