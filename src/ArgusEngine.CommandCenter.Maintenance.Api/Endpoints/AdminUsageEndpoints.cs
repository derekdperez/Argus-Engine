using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

public static class AdminUsageEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/admin/usage",
                () => Results.StatusCode(StatusCodes.Status410Gone))
            .WithName("ListAdminUsageDisabled")
            .WithTags("Admin");

        return app;
    }
}
