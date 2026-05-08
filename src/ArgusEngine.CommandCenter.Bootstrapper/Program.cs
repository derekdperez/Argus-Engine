using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CommandCenterBootstrapper");

await ArgusDbBootstrap.InitializeAsync(
        host.Services,
        host.Services.GetRequiredService<IConfiguration>(),
        logger,
        includeFileStore: true,
        CancellationToken.None)
    .ConfigureAwait(false);
