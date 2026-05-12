using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArgusEngine.CommandCenter.Operations.Api.Endpoints;

public static class CommandCenterStatusEndpoints
{
    public static IEndpointRouteBuilder MapCommandCenterStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/status/summary",
                () => Results.StatusCode(StatusCodes.Status410Gone))
            .WithName("GetCommandCenterStatusSummaryDisabled")
            .WithTags("Status");

        return app;
    }

    public static void Map(WebApplication app)
    {
        app.MapCommandCenterStatusEndpoints();
    }
}
