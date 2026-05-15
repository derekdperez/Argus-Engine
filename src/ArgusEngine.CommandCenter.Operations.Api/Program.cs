using ArgusEngine.CommandCenter.Operations.Api;
using ArgusEngine.CommandCenter.Operations.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOperationsApi(builder.Configuration);

var app = builder.Build();

app.MapOperationsApi();
app.MapProxyEndpoints();

await app.RunAsync().ConfigureAwait(false);
