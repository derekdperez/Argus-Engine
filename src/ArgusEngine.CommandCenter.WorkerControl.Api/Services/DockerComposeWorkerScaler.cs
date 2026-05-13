using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Services;

/// <summary>
/// Shared Docker Compose helper used by the autoscaler and the manual worker-scale endpoints.
/// Runs Compose through /bin/sh so the same command path works for Docker Compose V2 and legacy docker-compose.
/// </summary>
internal static class DockerComposeWorkerScaler
{
    private const string DefaultComposePath = "/home/derekdperez_dev/argus-engine/deploy/docker-compose.yml";
    private const string ContainerRepoRootFallback = "/workspace";
    private const string DefaultProjectName = "argus-engine";

    public static async Task<IReadOnlyList<DockerComposeContainerInfo>> GetComposeContainersAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        var projectName = GetComposeProjectName(configuration);
        var filter = $"label=com.docker.compose.project={projectName}";
        var command = $"docker ps -a --no-trunc --format '{{{{json .}}}}' --filter {ShQuote(filter)}";

        var result = await RunShellCommandAsync(command, TimeSpan.FromSeconds(15), logger, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"docker ps failed: {FirstNonEmpty(result.Error, result.Output, "no docker output")}");
        }

        var list = new List<DockerComposeContainerInfo>();

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = GetString(root, "ID");
                var name = GetString(root, "Names");
                var state = GetString(root, "State", "unknown");
                var status = GetString(root, "Status", state);
                var labels = GetString(root, "Labels");
                var serviceName = ExtractLabel(labels, "com.docker.compose.service");

                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(new DockerComposeContainerInfo(id, name, state, status, serviceName));
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Skipping malformed docker ps JSON line: {Line}", line);
            }
        }

        return list;
    }

    public static async Task<IReadOnlyDictionary<string, int>> GetRunningServiceCountsAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var containers = await GetComposeContainersAsync(configuration, logger, ct).ConfigureAwait(false);

        foreach (var container in containers)
        {
            if (string.IsNullOrWhiteSpace(container.ServiceName) ||
                !container.State.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            counts.TryGetValue(container.ServiceName, out var current);
            counts[container.ServiceName] = current + 1;
        }

        return counts;
    }

    public static async Task ScaleWorkerAsync(
        string serviceName,
        int desiredCount,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentOutOfRangeException.ThrowIfNegative(desiredCount);

        var composePath = ResolveExistingPath(
            GetComposeFilePath(configuration),
            Path.Combine(ContainerRepoRootFallback, "deploy", "docker-compose.yml"));

        var repoRoot = ResolveExistingPath(
            GetRepoRoot(configuration),
            ContainerRepoRootFallback,
            Path.GetDirectoryName(composePath) is { } composeDirectory
                ? Directory.GetParent(composeDirectory)?.FullName ?? composeDirectory
                : ContainerRepoRootFallback);

        var projectName = GetComposeProjectName(configuration);
        var scaleArg = $"{serviceName}={desiredCount.ToString(CultureInfo.InvariantCulture)}";

        var composeArgs = string.Join(
            " ",
            "up",
            "-d",
            "--no-build",
            "--no-deps",
            "--scale",
            ShQuote(scaleArg),
            ShQuote(serviceName));

        var command = BuildComposeCommand(repoRoot, projectName, composePath, composeArgs);

        logger.LogInformation(
            "Executing Docker Compose scale command for {ServiceName}: {Command}",
            serviceName,
            command);

        var result = await RunShellCommandAsync(command, TimeSpan.FromSeconds(120), logger, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"docker compose scale failed for {serviceName}: {FirstNonEmpty(result.Error, result.Output, "no docker output")}");
        }
    }

    public static string GetComposeProjectName(IConfiguration configuration) =>
        configuration["Argus:Autoscaler:DockerComposeProject"] ?? DefaultProjectName;

    public static string GetComposeFilePath(IConfiguration configuration) =>
        configuration["Argus:Autoscaler:DockerComposePath"] ?? DefaultComposePath;

    public static string GetRepoRoot(IConfiguration configuration)
    {
        var configuredRoot = configuration["Argus:Autoscaler:RepoRoot"];
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        var composePath = Path.GetFullPath(GetComposeFilePath(configuration));
        var composeDirectory = Path.GetDirectoryName(composePath);

        if (string.IsNullOrWhiteSpace(composeDirectory))
        {
            return "/home/derekdperez_dev/argus-engine";
        }

        return Directory.GetParent(composeDirectory)?.FullName ?? composeDirectory;
    }

    public static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : id.Length <= 12 ? id : id[..12];

    public static string FirstNonEmpty(params string?[] values)
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

    private static string ResolveExistingPath(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                (Directory.Exists(candidate) || File.Exists(candidate)))
            {
                return candidate;
            }
        }

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? ".";
    }

    private static string BuildComposeCommand(
        string repoRoot,
        string projectName,
        string composePath,
        string composeArgs)
    {
        return string.Join(
            " ",
            "cd",
            ShQuote(repoRoot),
            "&&",
            "if docker compose version >/dev/null 2>&1; then",
            "docker compose",
            "-p",
            ShQuote(projectName),
            "-f",
            ShQuote(composePath),
            composeArgs + ";",
            "elif command -v docker-compose >/dev/null 2>&1; then",
            "docker-compose",
            "-p",
            ShQuote(projectName),
            "-f",
            ShQuote(composePath),
            composeArgs + ";",
            "else",
            "echo 'Docker Compose is not available inside command-center-worker-control-api; install docker-cli-compose or docker-compose.' >&2;",
            "exit 127;",
            "fi");
    }

    private static async Task<CommandResult> RunShellCommandAsync(
        string command,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("-lc");
        process.StartInfo.ArgumentList.Add(command);

        try
        {
            if (!process.Start())
            {
                return new CommandResult(false, string.Empty, "process.Start() returned false");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return new CommandResult(true, stdout, stderr);
            }

            logger.LogWarning(
                "Shell command failed with exit code {ExitCode}. Command: {Command}. Stdout: {Stdout}. Stderr: {Stderr}",
                process.ExitCode,
                command,
                stdout,
                stderr);

            return new CommandResult(false, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new CommandResult(false, string.Empty, "command timed out or was cancelled");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new CommandResult(false, string.Empty, ex.Message);
        }
    }

    private static string GetString(JsonElement root, string propertyName, string defaultValue = "") =>
        root.TryGetProperty(propertyName, out var property) ? property.GetString() ?? defaultValue : defaultValue;

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

    private static string ShQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

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

    private sealed record CommandResult(bool Success, string Output, string Error);
}

internal sealed record DockerComposeContainerInfo(
    string Id,
    string Name,
    string State,
    string Status,
    string ServiceName);
