using ArgusEngine.CommandCenter.Operations.Api.Services;

namespace ArgusEngine.CommandCenter.Operations.Api.Endpoints;

public static class CommandCenterStatusEndpoints
{
    public static IEndpointRouteBuilder MapCommandCenterStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/status/summary",
                async (ICommandCenterStatusSnapshotService statusService, CancellationToken cancellationToken) =>
                {
                    var snapshot = await statusService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    return Results.Ok(snapshot);
                })
            .WithName("GetCommandCenterStatusSummary")
            .WithTags("Status");

        return app;
    }

    public static void Map(WebApplication app)
    {
        app.MapCommandCenterStatusEndpoints();
    }
}


