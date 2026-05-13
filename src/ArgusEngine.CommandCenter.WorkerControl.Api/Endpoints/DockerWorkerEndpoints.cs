using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.WorkerControl.Api.Services;
using ArgusEngine.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

/// <summary>
/// Docker Compose worker status and manual scaling endpoints.
/// </summary>
public static class DockerWorkerEndpoints
{
    private static readonly WorkerServiceDefinition[] ScalableServices =
    [
        new("worker-spider", "Spider Worker", [WorkerKeys.Spider]),
        new("worker-http-requester", "HTTP Requester", [WorkerKeys.HttpRequester]),
        new("worker-enum", "Enum Worker", [WorkerKeys.Enumeration]),
        new("worker-portscan", "Port Scan Worker", [WorkerKeys.PortScan]),
        new("worker-highvalue", "High Value Worker", [WorkerKeys.HighValueRegex, WorkerKeys.HighValuePaths]),
        new("worker-techid", "Tech ID Worker", [WorkerKeys.TechnologyIdentification]),
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
        ArgusDbContext db,
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
                            : running == 0 ? "stopped" : "partial";

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
            logger.LogWarning(ex, "Docker worker status query failed; falling back to worker heartbeat status.");

            var services = await BuildHeartbeatStatusAsync(db, at, ct).ConfigureAwait(false);

            return Results.Ok(new DockerWorkerStatusSnapshotDto(
                at,
                true,
                null,
                services));
        }
    }

    private static async Task<IReadOnlyList<DockerWorkerServiceDto>> BuildHeartbeatStatusAsync(
        ArgusDbContext db,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var cutoff = at.AddMinutes(-2);

        var heartbeats = await db.WorkerHeartbeats
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ScalableServices
            .Select(def =>
            {
                var matching = heartbeats
                    .Where(h => def.WorkerKeys.Contains(h.WorkerKey, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(h => h.WorkerKey)
                    .ThenBy(h => h.HostName)
                    .ToList();

                var containers = matching
                    .Select(h =>
                    {
                        var fresh = h.LastHeartbeatUtc >= cutoff;
                        var healthy = h.IsHealthy;
                        var active = fresh && healthy;
                        var state = active ? "running" : fresh ? "unhealthy" : "stale";
                        var color = active ? "green" : fresh ? "yellow" : "red";
                        var status = active
                            ? $"heartbeat {FormatAge(at - h.LastHeartbeatUtc)} ago"
                            : !string.IsNullOrWhiteSpace(h.HealthMessage)
                                ? h.HealthMessage!
                                : fresh
                                    ? "heartbeat reported unhealthy"
                                    : $"last heartbeat {FormatAge(at - h.LastHeartbeatUtc)} ago";

                        var name = string.IsNullOrWhiteSpace(h.HostName)
                            ? h.WorkerKey
                            : $"{h.WorkerKey}@{h.HostName}";

                        return new DockerWorkerContainerDto(
                            ShortHeartbeatId(h.WorkerKey, h.HostName, h.ProcessId),
                            name,
                            state,
                            status,
                            color);
                    })
                    .ToList();

                var running = containers.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
                var total = containers.Count;
                var overall = running == 0 && total == 0
                    ? "stopped"
                    : running == total
                        ? "running"
                        : running == 0 ? "stopped" : "partial";
                var color = overall == "running" ? "green" : overall == "partial" ? "yellow" : "red";

                return new DockerWorkerServiceDto(
                    def.ServiceName,
                    def.DisplayName,
                    running,
                    total,
                    overall,
                    color,
                    containers);
            })
            .ToList();
    }

    private static async Task<IResult> ScaleDockerWorkerAsync(
        string serviceName,
        DockerWorkerScaleRequest body,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DockerWorkerEndpoints");
        var def = ScalableServices.FirstOrDefault(d => string.Equals(d.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

        if (def is null)
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
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ? "green" :
        state.Equals("exited", StringComparison.OrdinalIgnoreCase) ? "red" :
        state.Equals("created", StringComparison.OrdinalIgnoreCase) ? "yellow" :
        "gray";

    private static string ShortHeartbeatId(string workerKey, string hostName, int processId)
    {
        var raw = $"{workerKey}:{hostName}:{processId}";
        var hash = raw.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return $"{Math.Abs(hash):x8}";
    }

    private static string FormatAge(TimeSpan age)
    {
        age = age < TimeSpan.Zero ? TimeSpan.Zero : age;

        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        return $"{Math.Max(1, (int)age.TotalHours)}h";
    }

    private sealed record WorkerServiceDefinition(
        string ServiceName,
        string DisplayName,
        string[] WorkerKeys);
}
