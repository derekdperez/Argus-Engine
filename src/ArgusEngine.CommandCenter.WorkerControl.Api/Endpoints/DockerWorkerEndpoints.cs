using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.WorkerControl.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

/// <summary>
/// Docker Compose worker status and manual scaling endpoints.
/// </summary>
public static class DockerWorkerEndpoints
{
    private static readonly (string ServiceName, string DisplayName)[] ScalableServices =
    [
        ("worker-spider", "Spider Worker"),
        ("worker-http-requester", "HTTP Requester"),
        ("worker-enum", "Enum Worker"),
        ("worker-portscan", "Port Scan Worker"),
        ("worker-highvalue", "High Value Worker"),
        ("worker-techid", "Tech ID Worker"),
    ];

    public static IEndpointRouteBuilder MapDockerWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workers/docker-status", GetDockerStatusAsync)
            .WithName("DockerWorkerStatus");

        app.MapPut("/api/workers/{serviceName}/docker-scale", ScaleDockerWorkerAsync)
            .WithName("DockerScaleWorker");

        return app;
    }

    private static async Task<IResult> GetDockerStatusAsync(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DockerWorkerEndpoints");
        var at = DateTimeOffset.UtcNow;

        try
        {
            var allContainers = await DockerComposeWorkerScaler.GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            var services = ScalableServices
                .Select(def =>
                {
                    var matching = allContainers
                        .Where(c => string.Equals(c.ServiceName, def.ServiceName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var containerDtos = matching
                        .Select(c =>
                        {
                            var color = ContainerStateColor(c.State);
                            return new DockerWorkerContainerDto(
                                DockerComposeWorkerScaler.ShortId(c.Id),
                                c.Name.TrimStart('/'),
                                c.State,
                                c.Status,
                                color);
                        })
                        .ToList();

                    var running = containerDtos.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
                    var overall = running == 0 && containerDtos.Count == 0
                        ? "stopped"
                        : running == containerDtos.Count
                            ? "running"
                            : running == 0
                                ? "stopped"
                                : "partial";
                    var color = overall == "running" ? "green" : overall == "partial" ? "yellow" : "red";

                    return new DockerWorkerServiceDto(
                        def.ServiceName,
                        def.DisplayName,
                        running,
                        containerDtos.Count,
                        overall,
                        color,
                        containerDtos);
                })
                .ToList();

            return Results.Ok(new DockerWorkerStatusSnapshotDto(at, true, null, services));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker worker status query failed.");

            var empty = ScalableServices
                .Select(def => new DockerWorkerServiceDto(
                    def.ServiceName,
                    def.DisplayName,
                    0,
                    0,
                    "unknown",
                    "gray",
                    Array.Empty<DockerWorkerContainerDto>()))
                .ToList();

            return Results.Ok(new DockerWorkerStatusSnapshotDto(at, false, ex.Message, empty));
        }
    }

    private static async Task<IResult> ScaleDockerWorkerAsync(
        string serviceName,
        DockerWorkerScaleRequest body,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DockerWorkerEndpoints");

        var def = ScalableServices.FirstOrDefault(d =>
            string.Equals(d.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

        if (def == default)
        {
            return Results.BadRequest($"Unknown scalable worker service: {serviceName}");
        }

        if (body.DesiredCount < 0 || body.DesiredCount > 50)
        {
            return Results.BadRequest("desiredCount must be between 0 and 50.");
        }

        try
        {
            var before = await DockerComposeWorkerScaler.GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            var previousCount = before.Count(c =>
                string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) &&
                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

            await DockerComposeWorkerScaler.ScaleWorkerAsync(serviceName, body.DesiredCount, configuration, logger, ct)
                .ConfigureAwait(false);

            await Task.Delay(1000, ct).ConfigureAwait(false);

            var after = await DockerComposeWorkerScaler.GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            var actualCount = after.Count(c =>
                string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) &&
                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

            var message = body.DesiredCount == previousCount
                ? $"{serviceName} already at {previousCount} running container(s); no change requested."
                : $"{serviceName} requested scale from {previousCount} to {body.DesiredCount}; {actualCount} running container(s) observed after scaling.";

            return Results.Ok(new DockerWorkerScaleResult(
                serviceName,
                previousCount,
                body.DesiredCount,
                actualCount,
                message));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker scale failed for {ServiceName}.", serviceName);

            return Results.Problem(
                title: "Docker scale failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string ContainerStateColor(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase)
            ? "green"
            : state.Equals("exited", StringComparison.OrdinalIgnoreCase)
                ? "red"
                : state.Equals("created", StringComparison.OrdinalIgnoreCase)
                    ? "yellow"
                    : "gray";
}
