using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NightmareV2.CommandCenter.Models;

namespace NightmareV2.CommandCenter;

internal static class DockerRuntimeStatusBuilder
{
    private const int LogTailLines = 300;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly ComponentDefinition[] Components =
    [
        new("command-center", "Command Center", "command-center"),
        new("postgres", "Postgres", "postgres"),
        new("redis", "Redis", "redis"),
        new("rabbitmq", "RabbitMQ", "rabbitmq"),
        new("gatekeeper", "Gatekeeper", "gatekeeper"),
        new("worker-spider", "Spider Worker", "worker-spider"),
        new("worker-enum", "Enum Worker", "worker-enum"),
        new("worker-portscan", "Port Scan Worker", "worker-portscan"),
        new("worker-highvalue", "High Value Worker", "worker-highvalue"),
    ];

    public static async Task<DockerRuntimeStatusDto> BuildAsync(CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var list = await RunCommandAsync(
                "docker",
                "ps -a --no-trunc --format \"{{json .}}\"",
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);

        if (!list.Success)
        {
            var unavailableComponents = Components
                .Select(
                    c => new DockerComponentHealthDto(
                        c.Key,
                        c.DisplayName,
                        "unknown",
                        "gray",
                        "docker runtime unavailable"))
                .ToList();

            return new DockerRuntimeStatusDto(
                at,
                false,
                "unknown",
                "gray",
                string.IsNullOrWhiteSpace(list.StdErr) ? list.StdOut : list.StdErr,
                unavailableComponents,
                [],
                []);
        }

        var psRows = ParsePsRows(list.StdOut);
        var containerTasks = psRows.Select(r => BuildContainerStatusAsync(r, cancellationToken));
        var containers = (await Task.WhenAll(containerTasks).ConfigureAwait(false))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var componentStatuses = BuildComponentStatuses(containers);
        var imageStatuses = BuildImageStatuses(containers);

        var overallStatus = SelectWorstStatus(componentStatuses.Select(x => x.Status));
        var overallColor = StatusToColor(overallStatus);

        return new DockerRuntimeStatusDto(
            at,
            true,
            overallStatus,
            overallColor,
            null,
            componentStatuses,
            imageStatuses,
            containers);
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
                    rows.Add(row);
            }
            catch
            {
                // Ignore malformed line and continue.
            }
        }

        return rows;
    }

    private static async Task<DockerContainerStatusDto> BuildContainerStatusAsync(PsRow row, CancellationToken cancellationToken)
    {
        var inspectTask = RunCommandAsync(
            "docker",
            $"inspect --type container --format \"{{{{if .State.Health}}}}{{{{.State.Health.Status}}}}{{{{else}}}}none{{{{end}}}}\" {row.ID}",
            TimeSpan.FromSeconds(10),
            cancellationToken);

        var logsTask = RunCommandAsync(
            "docker",
            $"logs --tail {LogTailLines} --timestamps {row.ID}",
            TimeSpan.FromSeconds(20),
            cancellationToken);

        await Task.WhenAll(inspectTask, logsTask).ConfigureAwait(false);

        var inspect = await inspectTask.ConfigureAwait(false);
        var logs = await logsTask.ConfigureAwait(false);

        var health = inspect.Success
            ? inspect.StdOut.Trim()
            : "unknown";
        if (string.IsNullOrWhiteSpace(health))
            health = "unknown";

        var status = ClassifyContainerStatus(row.Status, health);
        var color = StatusToColor(status);

        var logLines = logs.Success
            ? EnumerateLines(logs.StdOut).TakeLast(LogTailLines).ToList()
            : [ $"[log retrieval failed] {(string.IsNullOrWhiteSpace(logs.StdErr) ? logs.StdOut : logs.StdErr).Trim()}" ];

        return new DockerContainerStatusDto(
            row.ID,
            row.Names ?? string.Empty,
            row.Image ?? string.Empty,
            row.Status ?? string.Empty,
            health,
            status,
            color,
            logLines);
    }

    private static List<DockerComponentHealthDto> BuildComponentStatuses(IReadOnlyList<DockerContainerStatusDto> containers)
    {
        var rows = new List<DockerComponentHealthDto>(Components.Length);
        foreach (var component in Components)
        {
            var matches = containers
                .Where(c => c.Name.Contains(component.Match, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                rows.Add(new DockerComponentHealthDto(component.Key, component.DisplayName, "critical", "red", "container not found"));
                continue;
            }

            var worst = SelectWorstStatus(matches.Select(m => m.Status));
            var reason = worst switch
            {
                "healthy" => "running and healthy",
                "degraded" => "running with non-healthy signals",
                "critical" => "container stopped or unhealthy",
                _ => "status unknown",
            };
            rows.Add(new DockerComponentHealthDto(component.Key, component.DisplayName, worst, StatusToColor(worst), reason));
        }

        return rows;
    }

    private static List<DockerImageStatusDto> BuildImageStatuses(IReadOnlyList<DockerContainerStatusDto> containers)
    {
        return containers
            .GroupBy(c => c.Image, StringComparer.OrdinalIgnoreCase)
            .Select(
                g =>
                {
                    var total = g.LongCount();
                    var healthy = g.LongCount(c => c.Status == "healthy");
                    var degraded = g.LongCount(c => c.Status == "degraded");
                    var critical = g.LongCount(c => c.Status == "critical");
                    var status = critical > 0 ? "critical" : degraded > 0 ? "degraded" : "healthy";
                    return new DockerImageStatusDto(
                        g.Key,
                        total,
                        healthy,
                        degraded,
                        critical,
                        status,
                        StatusToColor(status));
                })
            .OrderBy(i => i.Image, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ClassifyContainerStatus(string? dockerStatusText, string? health)
    {
        var status = dockerStatusText ?? string.Empty;
        var healthStatus = (health ?? string.Empty).Trim();

        if (status.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
        {
            if (healthStatus.Equals("healthy", StringComparison.OrdinalIgnoreCase)
                || healthStatus.Equals("none", StringComparison.OrdinalIgnoreCase))
                return "healthy";
            if (healthStatus.Equals("starting", StringComparison.OrdinalIgnoreCase)
                || healthStatus.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                return "degraded";
            return "critical";
        }

        if (status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Dead", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Created", StringComparison.OrdinalIgnoreCase))
            return "critical";

        return "degraded";
    }

    private static string SelectWorstStatus(IEnumerable<string> statuses)
    {
        var worstRank = -1;
        var worst = "unknown";
        foreach (var status in statuses)
        {
            var rank = StatusRank(status);
            if (rank > worstRank)
            {
                worstRank = rank;
                worst = status;
            }
        }

        return worst;
    }

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
            yield return line;
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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

            if (!process.Start())
                return CommandResult.Fail("process start returned false");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var outTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdOut = await outTask.ConfigureAwait(false);
            var stdErr = await errTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
                return CommandResult.Fail(stdOut, stdErr);

            return CommandResult.Ok(stdOut, stdErr);
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

    private sealed record PsRow(
        string ID,
        string Image,
        string Status,
        string Names);

    private sealed record ComponentDefinition(string Key, string DisplayName, string Match);

    private sealed record CommandResult(bool Success, string StdOut, string StdErr)
    {
        public static CommandResult Ok(string stdOut, string stdErr) => new(true, stdOut, stdErr);

        public static CommandResult Fail(string stdOut, string stdErr = "") => new(false, stdOut, stdErr);
    }
}
