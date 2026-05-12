using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ArgusEngine.CommandCenter.Operations.Api.Endpoints;

public static class CommandCenterStatusEndpoints
{
    public static IEndpointRouteBuilder MapCommandCenterStatusEndpoints(this IEndpointRouteBuilder app)
    {
        // Disabled: these endpoints were used only by removed web application pages.
        return app;
    }
}
