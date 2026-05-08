using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.Operations.Api;

internal static class DockerRuntimeStatusBuilder
{
    private const int LogTailLines = 300;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly ComponentDefinition[] Components =
    [
        new("command-center", "Command Center", "command-center"),
        new("command-center-operations-api", "Operations API", "command-center-operations-api"),
        new("postgres", "Postgres", "postgres"),
        new("filestore-db-init", "FileStore DB Init", "filestore-db-init"),
        new("redis", "Redis", "redis"),
        new("rabbitmq", "RabbitMQ", "rabbitmq"),
        new("gatekeeper", "Gatekeeper", "gatekeeper"),
        new("worker-spider", "Spider Worker", "worker-spider"),
        new("worker-enum", "Enum Worker", "worker-enum"),
        new("worker-portscan", "Port Scan Worker", "worker-portscan"),
        new("worker-highvalue", "High Value Worker", "worker-highvalue"),
        new("worker-techid", "Technology Identification Worker", "worker-techid"),
    ];

    public static async Task<DockerRuntimeStatusDto> BuildAsync(CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var list = await RunCommandAsync("docker", "ps -a --no-trunc --format \"{{json .}}\"", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!list.Success)
        {
            return new DockerRuntimeStatusDto(
                at,
                false,
                "unknown",
                "gray",
                string.IsNullOrWhiteSpace(list.StdErr) ? list.StdOut : list.StdErr,
                Components.Select(c => new DockerComponentHealthDto(c.Key, c.DisplayName, "-", null, "unknown", "gray", "docker runtime unavailable")).ToList(),
                [],
                []);
        }

        var rows = ParsePsRows(list.StdOut);
        var containers = (await Task.WhenAll(rows.Select(row => BuildContainerStatusAsync(row, cancellationToken))).ConfigureAwait(false))
            .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var componentStatuses = BuildComponentStatuses(containers);
        var imageStatuses = BuildImageStatuses(containers);
        var overall = SelectWorstStatus(componentStatuses.Select(x => x.Status));

        return new DockerRuntimeStatusDto(at, true, overall, StatusToColor(overall), null, componentStatuses, imageStatuses, containers);
    }

    private static List<PsRow> ParsePsRows(string stdOut)
    {
        var rows = new List<PsRow>();
        foreach (var line in EnumerateLines(stdOut))
        {
            try
            {
                var row = JsonSerializer.Deserialize<PsRow>(line, Json);
                if (row is not null && !string.IsNullOrWhiteSpace(row.ID))
                {
                    rows.Add(row);
                }
            }
            catch
            {
                // Ignore malformed docker output lines.
            }
        }

        return rows;
    }

    private static async Task<DockerContainerStatusDto> BuildContainerStatusAsync(PsRow row, CancellationToken cancellationToken)
    {
        var inspectTask = RunCommandAsync("docker", $"inspect --type container --format \"{{{{if .State.Health}}}}{{{{.State.Health.Status}}}}{{{{else}}}}none{{{{end}}}}\" {row.ID}", TimeSpan.FromSeconds(10), cancellationToken);
        var logsTask = RunCommandAsync("docker", $"logs --tail {LogTailLines} --timestamps {row.ID}", TimeSpan.FromSeconds(20), cancellationToken);
        var imageTask = RunCommandAsync("docker", $"image inspect --format \"{{{{.Created}}}}\" {row.Image}", TimeSpan.FromSeconds(10), cancellationToken);

        await Task.WhenAll(inspectTask, logsTask, imageTask).ConfigureAwait(false);
        var inspect = await inspectTask.ConfigureAwait(false);
        var logs = await logsTask.ConfigureAwait(false);
        var image = await imageTask.ConfigureAwait(false);
        var health = inspect.Success ? inspect.StdOut.Trim() : "unknown";
        if (string.IsNullOrWhiteSpace(health)) health = "unknown";
        var status = ClassifyContainerStatus(row.Names, row.Status, health);
        var combinedLogs = (logs.StdOut ?? "") + (logs.StdErr ?? "");
        var logLines = logs.Success
            ? EnumerateLines(combinedLogs).TakeLast(LogTailLines).ToList()
            : [$"[log retrieval failed] {(string.IsNullOrWhiteSpace(logs.StdErr) ? logs.StdOut ?? "" : logs.StdErr).Trim()}"];

        return new DockerContainerStatusDto(
            row.ID,
            row.Names ?? string.Empty,
            row.Image ?? string.Empty,
            ExtractVersion(row.Image, row.Labels),
            TryParseDockerDate(image.Success ? image.StdOut.Trim() : ""),
            row.Status ?? string.Empty,
            health,
            status,
            StatusToColor(status),
            logLines);
    }

    private static List<DockerComponentHealthDto> BuildComponentStatuses(IReadOnlyList<DockerContainerStatusDto> containers) =>
        Components.Select(
                component =>
                {
                    var matches = containers.Where(c => c.Name.Contains(component.Match, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                    {
                        return new DockerComponentHealthDto(component.Key, component.DisplayName, "-", null, "critical", "red", "container not found");
                    }

                    var worst = SelectWorstStatus(matches.Select(m => m.Status));
                    var firstReleased = matches.Where(m => m.ImageCreatedAtUtc is not null).Select(m => m.ImageCreatedAtUtc!.Value).DefaultIfEmpty().Min();
                    return new DockerComponentHealthDto(
                        component.Key,
                        component.DisplayName,
                        matches.Select(m => m.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "-",
                        firstReleased == default ? null : firstReleased,
                        worst,
                        StatusToColor(worst),
                        worst == "healthy" ? "running and healthy" : "container stopped or unhealthy");
                })
            .ToList();

    private static List<DockerImageStatusDto> BuildImageStatuses(IReadOnlyList<DockerContainerStatusDto> containers) =>
        containers.GroupBy(c => c.Image, StringComparer.OrdinalIgnoreCase)
            .Select(
                group =>
                {
                    var firstReleased = group.Where(c => c.ImageCreatedAtUtc is not null).Select(c => c.ImageCreatedAtUtc!.Value).DefaultIfEmpty().Min();
                    var total = group.LongCount();
                    var healthy = group.LongCount(c => c.Status == "healthy");
                    var degraded = group.LongCount(c => c.Status == "degraded");
                    var critical = group.LongCount(c => c.Status == "critical");
                    var status = critical > 0 ? "critical" : degraded > 0 ? "degraded" : "healthy";
                    return new DockerImageStatusDto(group.Key, group.Select(c => c.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "-", firstReleased == default ? null : firstReleased, total, healthy, degraded, critical, status, StatusToColor(status));
                })
            .OrderBy(image => image.Image, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ClassifyContainerStatus(string? containerName, string? dockerStatusText, string? health)
    {
        var status = dockerStatusText ?? string.Empty;
        var healthStatus = (health ?? string.Empty).Trim();
        if (status.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
        {
            return healthStatus.Equals("healthy", StringComparison.OrdinalIgnoreCase) || healthStatus.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? "healthy"
                : healthStatus.Equals("starting", StringComparison.OrdinalIgnoreCase) || healthStatus.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    ? "degraded"
                    : "critical";
        }

        if (status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase) && containerName?.Contains("filestore-db-init", StringComparison.OrdinalIgnoreCase) == true && status.Contains("(0)", StringComparison.Ordinal))
        {
            return "healthy";
        }

        return status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Dead", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Created", StringComparison.OrdinalIgnoreCase)
            ? "critical"
            : "degraded";
    }

    private static string ExtractVersion(string? image, string? labels)
    {
        if (!string.IsNullOrWhiteSpace(labels))
        {
            foreach (var part in labels.Split(','))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("org.opencontainers.image.version", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv[1]))
                {
                    return kv[1].Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(image)) return "-";
        var atIndex = image.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
        {
            var digest = image[(atIndex + 1)..];
            return digest.Length > 18 ? digest[..18] : digest;
        }

        var lastSlash = image.LastIndexOf('/');
        var lastColon = image.LastIndexOf(':');
        return lastColon > lastSlash ? image[(lastColon + 1)..] : "latest";
    }

    private static DateTimeOffset? TryParseDockerDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string SelectWorstStatus(IEnumerable<string> statuses) =>
        statuses.Select(status => (Status: status, Rank: StatusRank(status))).DefaultIfEmpty(("unknown", 1)).MaxBy(x => x.Item2).Item1;

    private static int StatusRank(string status) =>
        status switch
        {
            "critical" => 3,
            "degraded" => 2,
            "unknown" => 1,
            "healthy" => 0,
            _ => 1,
        };

    private static string StatusToColor(string status) =>
        status switch
        {
            "healthy" => "green",
            "degraded" => "yellow",
            "critical" => "red",
            _ => "gray",
        };

    private static IEnumerable<string> EnumerateLines(string value)
    {
        using var reader = new StringReader(value ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
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

            if (!process.Start()) return CommandResult.Fail("process start returned false");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var outTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdOut = await outTask.ConfigureAwait(false);
            var stdErr = await errTask.ConfigureAwait(false);
            return process.ExitCode == 0 ? CommandResult.Ok(stdOut, stdErr) : CommandResult.Fail(stdOut, stdErr);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("command timeout/cancelled");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }

    private sealed record PsRow(string ID, string Image, string Status, string Names, string? Labels);

    private sealed record ComponentDefinition(string Key, string DisplayName, string Match);

    private sealed record CommandResult(bool Success, string StdOut, string StdErr)
    {
        public static CommandResult Ok(string stdOut, string stdErr) => new(true, stdOut, stdErr);

        public static CommandResult Fail(string stdOut, string stdErr = "") => new(false, stdOut, stdErr);
    }
}
