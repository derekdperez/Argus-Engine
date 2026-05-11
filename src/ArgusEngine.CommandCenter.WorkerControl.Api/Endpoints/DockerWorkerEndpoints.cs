using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

/// <summary>
/// Docker Compose worker status and manual scaling endpoints.
/// These are used when Argus is deployed locally, such as on a VM with docker compose,
/// rather than on ECS or Cloud Run.
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
        app.MapGet(
                "/api/workers/docker-status",
                async (IConfiguration configuration, CancellationToken ct) =>
                {
                    var at = DateTimeOffset.UtcNow;

                    try
                    {
                        var allContainers = await GetComposeContainersAsync(configuration, ct).ConfigureAwait(false);

                        var services = ScalableServices.Select(def =>
                        {
                            var matching = allContainers
                                .Where(c => string.Equals(c.ServiceName, def.ServiceName, StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            var containerDtos = matching.Select(c =>
                            {
                                var color = ContainerStateColor(c.State);
                                return new DockerWorkerContainerDto(
                                    ShortId(c.Id),
                                    c.Name.TrimStart('/'),
                                    c.State,
                                    c.State,
                                    color);
                            }).ToList();

                            var running = containerDtos.Count(c =>
                                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

                            var overall = running == 0 && containerDtos.Count == 0
                                ? "stopped"
                                : running == containerDtos.Count
                                    ? "running"
                                    : running == 0
                                        ? "stopped"
                                        : "partial";

                            var color = overall == "running"
                                ? "green"
                                : overall == "partial"
                                    ? "yellow"
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
                })
            .WithName("DockerWorkerStatus");

        app.MapPut(
                "/api/workers/{serviceName}/docker-scale",
                async (
                    string serviceName,
                    DockerWorkerScaleRequest body,
                    IConfiguration configuration,
                    CancellationToken ct) =>
                {
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
                        var before = await GetComposeContainersAsync(configuration, ct).ConfigureAwait(false);
                        var current = before
                            .Where(c => string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var previousCount = current.Count(c =>
                            c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

                        await RunDockerComposeScaleAsync(serviceName, body.DesiredCount, configuration, ct)
                            .ConfigureAwait(false);

                        await Task.Delay(800, ct).ConfigureAwait(false);

                        var after = await GetComposeContainersAsync(configuration, ct).ConfigureAwait(false);
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
                        return Results.Problem(
                            title: "Docker scale failed",
                            detail: ex.Message,
                            statusCode: StatusCodes.Status500InternalServerError);
                    }
                })
            .WithName("DockerScaleWorker");

        return app;
    }

    private static async Task<List<(string Id, string Name, string State, string? ServiceName)>> GetComposeContainersAsync(
        IConfiguration configuration,
        CancellationToken ct)
    {
        var projectName = GetComposeProjectName(configuration);
        var result = await RunCommandAsync(
                "docker",
                $"ps -a --no-trunc --format \"{{{{json .}}}}\" --filter label=com.docker.compose.project={projectName}",
                TimeSpan.FromSeconds(10),
                ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"docker ps failed: {FirstNonEmpty(result.Error, result.Output, "no docker output")}");
        }

        var list = new List<(string Id, string Name, string State, string? ServiceName)>();

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.TryGetProperty("ID", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                var name = root.TryGetProperty("Names", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var state = root.TryGetProperty("State", out var stateProp) ? stateProp.GetString() ?? "unknown" : "unknown";
                var labels = root.TryGetProperty("Labels", out var labelsProp) ? labelsProp.GetString() ?? string.Empty : string.Empty;
                var serviceName = ExtractLabel(labels, "com.docker.compose.service");

                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add((id, name, state, serviceName));
                }
            }
            catch (JsonException)
            {
                // Ignore malformed docker ps lines and keep the page usable.
            }
        }

        return list;
    }

    private static async Task RunDockerComposeScaleAsync(
        string serviceName,
        int count,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var composePath = configuration["Argus:Autoscaler:DockerComposePath"]
            ?? "/home/derekdperez_dev/argus-engine/deploy/docker-compose.yml";

        var projectName = GetComposeProjectName(configuration);

        var args =
            $"compose -p {projectName} -f {composePath} up -d --no-build --no-deps --scale {serviceName}={count.ToString(CultureInfo.InvariantCulture)} {serviceName}";

        var result = await RunCommandAsync("docker", args, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"docker compose scale failed: {FirstNonEmpty(result.Error, result.Output, "no docker output")}");
        }
    }

    private static string GetComposeProjectName(IConfiguration configuration) =>
        configuration["Argus:Autoscaler:DockerComposeProject"] ?? "argus-engine";

    private static string ExtractLabel(string labelsString, string key)
    {
        if (string.IsNullOrWhiteSpace(labelsString))
        {
            return string.Empty;
        }

        foreach (var part in labelsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();

            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return string.Empty;
    }

    private static string ContainerStateColor(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase)
            ? "green"
            : state.Equals("exited", StringComparison.OrdinalIgnoreCase)
                ? "red"
                : state.Equals("created", StringComparison.OrdinalIgnoreCase)
                    ? "yellow"
                    : "gray";

    private static async Task<(bool Success, string Output, string Error)> RunCommandAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            if (!process.Start())
            {
                return (false, string.Empty, "process.Start() returned false");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? (true, stdout, stderr)
                : (false, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (false, string.Empty, "command timed out or was cancelled");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return (false, string.Empty, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? string.Empty
            : id.Length <= 12
                ? id
                : id[..12];

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}
