using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

public static class AdminUsageEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsageEndpoints(this IEndpointRouteBuilder app)
    {
        // Disabled: these endpoints were used only by removed web application pages.
        return app;
    }
}
