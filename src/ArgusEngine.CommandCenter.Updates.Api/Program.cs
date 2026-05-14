using ArgusEngine.CommandCenter.Updates.Api.Endpoints;
using ArgusEngine.CommandCenter.Updates.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddComponentUpdateServices(builder.Configuration);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();

app.MapGet(
        "/health/ready",
        (
            IOptions<ComponentUpdaterOptions> options,
            IComponentUpdateService componentUpdateService) =>
        {
            var updaterOptions = options.Value;

            return Results.Ok(
                new
                {
                    status = "ready",
                    componentUpdater = new
                    {
                        updaterOptions.Enabled,
                        updaterOptions.RepositoryPath,
                        updaterOptions.ComposeFilePath,
                        updaterOptions.GitRemote,
                        updaterOptions.MainBranch,
                        updaterOptions.RequireCleanWorkingTree,
                        updaterOptions.LogLimit,
                        updaterOptions.CommandTimeoutSeconds
                    },
                    componentUpdateService = componentUpdateService.GetType().Name
                });
        })
    .AllowAnonymous();

app.MapComponentUpdateEndpoints();

await app.RunAsync().ConfigureAwait(false);
