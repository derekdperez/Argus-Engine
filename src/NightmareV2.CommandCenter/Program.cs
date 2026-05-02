using NightmareV2.CommandCenter.Components;
using NightmareV2.CommandCenter.Endpoints;
using NightmareV2.CommandCenter.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCommandCenterServices(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.InitializeCommandCenterDatabasesAsync().ConfigureAwait(false);

app.UseCommandCenterMiddleware();

app.MapCommandCenterEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);
