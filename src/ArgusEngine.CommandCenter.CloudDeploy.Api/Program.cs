using ArgusEngine.CloudDeploy;
using ArgusEngine.CommandCenter.CloudDeploy.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGcpHybridDeploy(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/healthz");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapCloudDeployEndpoints();

app.Run();
