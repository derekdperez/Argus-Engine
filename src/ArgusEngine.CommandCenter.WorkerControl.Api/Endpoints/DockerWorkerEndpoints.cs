using System.Text.Json;
using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

/// <summary>
/// Docker Compose worker status and manual scaling endpoints.
/// These are used when Argus is deployed locally (e.g. GCP VM with docker compose)
/// rather than on ECS/Cloud Run.
/// </summary>
public static class DockerWorkerEndpoints
{
    // Worker services that are scalable in docker-compose
    private static readonly (string ServiceName, string DisplayName)[] ScalableServices =
    [
        ("worker-spider",         "Spider Worker"),
        ("worker-http-requester", "HTTP Requester"),
        ("worker-enum",           "Enum Worker"),
        ("worker-portscan",       "Port Scan Worker"),
        ("worker-highvalue",      "High Value Worker"),
        ("worker-techid",         "Tech ID Worker"),
    ];

    public static IEndpointRouteBuilder MapDockerWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/workers/docker-status
        // Returns the live container state for every scalable worker service.
        app.MapGet(
            "/api/workers/docker-status",
            async (CancellationToken ct) =>
            {
                var at = DateTimeOffset.UtcNow;
                try
                {
                    var allContainers = await GetComposeContainersAsync(ct).ConfigureAwait(false);

                    var services = ScalableServices.Select(def =>
                    {
                        var matching = allContainers
                            .Where(c => string.Equals(c.ServiceName, def.ServiceName, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var containerDtos = matching.Select(c =>
                        {
                            var color = ContainerStateColor(c.State);
                            return new DockerWorkerContainerDto(
                                c.Id[..Math.Min(12, c.Id.Length)],
                                c.Name.TrimStart('/'),
                                c.State,
                                c.State,
                                color);
                        }).ToList();

                        var running = containerDtos.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
                        var overall = running == 0 && containerDtos.Count == 0 ? "stopped"
                            : running == containerDtos.Count ? "running"
                            : running == 0 ? "stopped"
                            : "partial";
                        var color = overall == "running" ? "green"
                            : overall == "partial" ? "yellow"
                            : "red";

                        return new DockerWorkerServiceDto(
                            def.ServiceName,
                            def.DisplayName,
                            running,
                            containerDtos.Count,
                            overall,
                            color,
                            containerDtos);
                    }).ToList();

                    return Results.Ok(new DockerWorkerStatusSnapshotDto(at, true, null, services));
                }
                catch (Exception ex)
                {
                    var empty = ScalableServices.Select(def =>
                        new DockerWorkerServiceDto(def.ServiceName, def.DisplayName, 0, 0, "unknown", "gray", [])).ToList();
                    return Results.Ok(new DockerWorkerStatusSnapshotDto(at, false, ex.Message, empty));
                }
            })
            .WithName("DockerWorkerStatus");

        // PUT /api/workers/{serviceName}/docker-scale
        // Scale a docker compose worker service to the requested count.
        app.MapPut(
            "/api/workers/{serviceName}/docker-scale",
            async (string serviceName, DockerWorkerScaleRequest body, CancellationToken ct) =>
            {
                var def = ScalableServices.FirstOrDefault(d =>
                    string.Equals(d.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

                if (def == default)
                    return Results.BadRequest($"Unknown scalable worker service: {serviceName}");

                if (body.DesiredCount < 0 || body.DesiredCount > 50)
                    return Results.BadRequest("desiredCount must be between 0 and 50.");

                try
                {
                    var before = await GetComposeContainersAsync(ct).ConfigureAwait(false);
                    var current = before.Where(c =>
                        string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)).ToList();
                    var previousCount = current.Count;
                    var desiredCount = body.DesiredCount;

                    if (desiredCount > previousCount)
                    {
                        // Scale up: start new containers using docker compose scale
                        await RunDockerComposeScaleAsync(serviceName, desiredCount, ct).ConfigureAwait(false);
                    }
                    else if (desiredCount < previousCount)
                    {
                        // Scale down: stop excess containers
                        await RunDockerComposeScaleAsync(serviceName, desiredCount, ct).ConfigureAwait(false);
                    }

                    // Re-read actual container count after scaling
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    var after = await GetComposeContainersAsync(ct).ConfigureAwait(false);
                    var actualCount = after.Count(c =>
                        string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
                        && c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

                    var message = desiredCount == previousCount
                        ? $"{serviceName} already at {previousCount} container(s); no change."
                        : $"{serviceName} scaled from {previousCount} to {actualCount} running container(s).";

                    return Results.Ok(new DockerWorkerScaleResult(
                        serviceName, previousCount, desiredCount, actualCount, message));
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        title: "Docker scale failed",
                        detail: ex.Message,
                        statusCode: 500);
                }
            })
            .WithName("DockerScaleWorker");

        return app;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<(string Id, string Name, string State, string? ServiceName)>> GetComposeContainersAsync(CancellationToken ct)
    {
        // Call docker via CLI (same mechanism as DockerRuntimeStatusBuilder).
        // Using docker ps with label filter is the most reliable cross-platform approach
        // from inside a container that has the docker socket mounted.
        var result = await RunCommandAsync(
            "docker",
            "ps -a --no-trunc --format \"{{json .}}\" --filter label=com.docker.compose.project=argus-engine",
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException($"docker ps failed: {result.Error}");

        var list = new List<(string Id, string Name, string State, string? ServiceName)>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("ID", out var idProp) ? idProp.GetString() ?? "" : "";
                var name = root.TryGetProperty("Names", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var state = root.TryGetProperty("State", out var stateProp) ? stateProp.GetString() ?? "unknown" : "unknown";
                var labels = root.TryGetProperty("Labels", out var labelsProp) ? labelsProp.GetString() ?? "" : "";
                var serviceName = ExtractLabel(labels, "com.docker.compose.service");
                if (!string.IsNullOrWhiteSpace(id))
                    list.Add((id, name, state, serviceName));
            }
            catch { /* skip malformed line */ }
        }
        return list;
    }

    private static async Task RunDockerComposeScaleAsync(string serviceName, int count, CancellationToken ct)
    {
        // Try docker compose (V2) first, fall back to docker-compose (V1)
        var args = $"compose -f /home/derekdperez_dev/argus-engine/deploy/docker-compose.yml up -d --no-build --scale {serviceName}={count} --no-recreate {serviceName}";
        var result = await RunCommandAsync("docker", args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"docker compose scale failed: {result.Error}");
    }

    private static string ExtractLabel(string labelsString, string key)
    {
        // Labels from docker ps --format json are a comma-separated "key=value" string
        if (string.IsNullOrWhiteSpace(labelsString)) return "";
        foreach (var part in labelsString.Split(','))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return "";
    }

    private static string ContainerStateColor(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ? "green"
        : state.Equals("exited", StringComparison.OrdinalIgnoreCase) ? "red"
        : state.Equals("created", StringComparison.OrdinalIgnoreCase) ? "yellow"
        : "gray";

    private static async Task<(bool Success, string Output, string Error)> RunCommandAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            if (!process.Start()) return (false, "", "process.Start() returned false");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? (true, stdout, stderr)
                : (false, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            return (false, "", "command timed out or was cancelled");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}
