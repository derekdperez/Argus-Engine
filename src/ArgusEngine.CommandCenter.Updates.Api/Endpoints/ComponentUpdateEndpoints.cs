using ArgusEngine.CommandCenter.Services.Updates;

namespace ArgusEngine.CommandCenter.Updates.Api.Endpoints;

public static class ComponentUpdateEndpoints
{
    public static IEndpointRouteBuilder MapComponentUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/development/components")
            .WithTags("Development");

        group.MapGet("/", async (
            IComponentUpdateService service,
            CancellationToken cancellationToken) =>
        {
            var components = await service.GetComponentsAsync(cancellationToken);
            return Results.Ok(components);
        });

        group.MapGet("/logs", async (
            int? limit,
            IComponentUpdateService service,
            CancellationToken cancellationToken) =>
        {
            var logs = await service.GetLogsAsync(limit, cancellationToken);
            return Results.Ok(logs);
        });

        group.MapPost("/{componentKey}/update", async (
            string componentKey,
            IComponentUpdateService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateComponentAsync(componentKey, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.Problem(result.Message, statusCode: StatusCodes.Status500InternalServerError);
        });

        return app;
    }
}

