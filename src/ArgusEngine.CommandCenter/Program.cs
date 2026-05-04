using ArgusEngine.CommandCenter.Components;
using ArgusEngine.CommandCenter.Endpoints;
using ArgusEngine.CommandCenter.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCommandCenterServices(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.InitializeCommandCenterDatabasesAsync().ConfigureAwait(false);

app.UseCommandCenterMiddleware();
app.MapCommandCenterEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);