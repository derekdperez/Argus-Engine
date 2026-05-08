using ArgusEngine.Infrastructure;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
