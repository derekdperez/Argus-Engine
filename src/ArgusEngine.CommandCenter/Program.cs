using System.Net;
using ArgusEngine.CommandCenter.Components;
using ArgusEngine.CommandCenter.Endpoints;
using ArgusEngine.CommandCenter.Startup;
using ArgusEngine.Workers.Spider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient.spider", LogLevel.Warning);

builder.Services.AddCommandCenterServices(builder.Configuration, builder.Environment);

// The HTTP request queue is written by the web application, so keep at least one
// in-process dispatcher running with CommandCenter. Dedicated worker-spider
// containers can still run alongside this because the worker leases rows with
// FOR UPDATE SKIP LOCKED.
builder.Services.AddOptions<SpiderHttpOptions>()
    .Bind(builder.Configuration.GetSection("Spider:Http"))
    .Validate(options => options.MaxConcurrency > 0, "Spider:Http:MaxConcurrency must be greater than zero.")
    .Validate(options => options.VisibilityTimeoutSeconds > 0, "Spider:Http:VisibilityTimeoutSeconds must be greater than zero.")
    .Validate(options => options.PollIntervalSeconds > 0, "Spider:Http:PollIntervalSeconds must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddSingleton<AdaptiveConcurrencyController>();
builder.Services.AddHostedService<HttpRequestQueueWorker>();

builder.Services.AddHttpClient("spider")
    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<SpiderHttpOptions>>().Value;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = false,
        };

        if (options.AllowInsecureSsl)
        {
#pragma warning disable CA5359
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#pragma warning restore CA5359
        }

        return handler;
    });

var app = builder.Build();

await app.InitializeCommandCenterDatabasesAsync().ConfigureAwait(false);

app.UseCommandCenterMiddleware();
app.MapCommandCenterEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);
