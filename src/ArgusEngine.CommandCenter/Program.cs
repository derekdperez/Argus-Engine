using ArgusEngine.CommandCenter.Components;
using ArgusEngine.CommandCenter.Endpoints;
using ArgusEngine.CommandCenter.Middleware;
using ArgusEngine.CommandCenter.Services.DeveloperAutomation;
using ArgusEngine.CommandCenter.Startup;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient.spider", LogLevel.Warning);

builder.Services.AddCommandCenterServices(builder.Configuration, builder.Environment);
builder.Services.AddMemoryCache();
builder.Services.AddDeveloperAutomationServices(builder.Configuration);

var app = builder.Build();

await app.InitializeCommandCenterDatabasesAsync().ConfigureAwait(false);

app.UseCommandCenterMiddleware();
app.UseMiddleware<OperationsApiResponseCacheMiddleware>();

app.MapCommandCenterEndpoints();
app.MapDeveloperAutomationEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);
