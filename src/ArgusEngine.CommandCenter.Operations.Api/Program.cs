using ArgusEngine.CommandCenter.Operations.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOperationsApi(builder.Configuration);

var app = builder.Build();

app.MapOperationsApi();

await app.RunAsync().ConfigureAwait(false);
