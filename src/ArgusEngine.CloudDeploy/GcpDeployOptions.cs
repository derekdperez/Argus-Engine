using System.Diagnostics;
using System.Globalization;

namespace ArgusEngine.CloudDeploy;

/// <summary>
/// Configuration for the GCP hybrid deployment strategy.
/// Bind from appsettings.json under the "GcpDeploy" section.
/// </summary>
public sealed class GcpDeployOptions
{
    public const string Section = "GcpDeploy";

    // ── GCP project ───────────────────────────────────────────────────────────
    public string ProjectId { get; set; } = string.Empty;

    public string Region { get; set; } = "us-central1";

    // ── Artifact Registry ─────────────────────────────────────────────────────
    /// <summary>GAR repository name (created if absent).</summary>
    public string GarRepository { get; set; } = "argus-engine";

    /// <summary>
    /// Fully-qualified image prefix.
    /// Defaults to "{Region}-docker.pkg.dev/{ProjectId}/{GarRepository}".
    /// Override only if using a different registry.
    /// </summary>
    public string? ImagePrefix { get; set; }

    public string ResolvedImagePrefix =>
        ImagePrefix ?? $"{Region}-docker.pkg.dev/{ProjectId}/{GarRepository}";

    /// <summary>
    /// Docker tag applied to all built images.
    /// Leave blank to generate a pinned deploy tag from VERSION and the current git SHA.
    /// Do not use "latest"; startup validation rejects it.
    /// </summary>
    public string ImageTag { get; set; } = string.Empty;

    public string ResolvedImageTag =>
        string.IsNullOrWhiteSpace(ImageTag)
            ? ImageTagDefaults.Create(RepoRoot)
            : ImageTagDefaults.Sanitize(ImageTag);

    // ── Worker build layout ───────────────────────────────────────────────────
    /// <summary>
    /// Worker project paths, keyed by WorkerType enum name.
    /// These are infrastructure/build-layout settings and intentionally do not live on WorkerType.
    /// </summary>
    public IDictionary<string, string> WorkerProjectPaths { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(WorkerType.Enumeration)] =
                "src/ArgusEngine.Workers.Enumeration/ArgusEngine.Workers.Enumeration.csproj",
            [nameof(WorkerType.Spider)] =
                "src/ArgusEngine.Workers.Spider/ArgusEngine.Workers.Spider.csproj",
            [nameof(WorkerType.HttpRequester)] =
                "src/ArgusEngine.Workers.HttpRequester/ArgusEngine.Workers.HttpRequester.csproj",
            [nameof(WorkerType.PortScan)] =
                "src/ArgusEngine.Workers.PortScan/ArgusEngine.Workers.PortScan.csproj",
            [nameof(WorkerType.HighValue)] =
                "src/ArgusEngine.Workers.HighValue/ArgusEngine.Workers.HighValue.csproj",
            [nameof(WorkerType.TechnologyIdentification)] =
                "src/ArgusEngine.Workers.TechnologyIdentification/ArgusEngine.Workers.TechnologyIdentification.csproj",
        };

    // ── Worker scaling defaults ───────────────────────────────────────────────
    public int WorkerMinInstances { get; set; } = 2;

    public int WorkerMaxInstances { get; set; } = 10;

    public int WorkerConcurrency { get; set; } = 4;

    public string WorkerCpu { get; set; } = "1";

    public string WorkerMemory { get; set; } = "512Mi";

    // ── Connectivity: how Cloud Run workers reach the local host ─────────────
    /// <summary>
    /// Public IP or hostname of the machine running the core services.
    /// Workers on Cloud Run need this to reach RabbitMQ, Postgres, Redis.
    /// </summary>
    public string HostPublicAddress { get; set; } = string.Empty;

    public string RabbitMqPublicUrl { get; set; } = string.Empty;

    public string PostgresPublicUrl { get; set; } = string.Empty;

    public string RedisPublicUrl { get; set; } = string.Empty;

    /// <summary>Base URL of the CommandCenter API, reachable from Cloud Run.</summary>
    public string CommandCenterApiUrl { get; set; } = string.Empty;

    // ── Local core compose ────────────────────────────────────────────────────
    /// <summary>
    /// Path to the docker-compose file for local core services.
    /// Defaults to "deployment/docker-compose.yml" relative to RepoRoot.
    /// </summary>
    public string CoreComposeFile { get; set; } = "deployment/docker-compose.yml";

    /// <summary>
    /// Absolute path to the repository root. Required for building images and
    /// locating compose files. Set automatically by DeployUi; override if
    /// running from a non-root working directory.
    /// </summary>
    public string RepoRoot { get; set; } = Directory.GetCurrentDirectory();

    public string GetWorkerProjectPath(WorkerType worker)
    {
        if (TryGetWorkerProjectPath(worker, out var projectPath))
            return projectPath;

        throw new InvalidOperationException(
            $"No GcpDeploy:WorkerProjectPaths mapping was configured for worker '{worker}'.");
    }

    public string GetWorkerProjectDirectory(WorkerType worker)
    {
        var projectPath = GetWorkerProjectPath(worker);
        var fileName = Path.GetFileNameWithoutExtension(projectPath);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException(
                $"GcpDeploy:WorkerProjectPaths:{worker} must end with a .csproj file name.");
        }

        return fileName;
    }

    public string GetWorkerAppDll(WorkerType worker) =>
        $"{GetWorkerProjectDirectory(worker)}.dll";

    // ── Validation ────────────────────────────────────────────────────────────
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectId))
            yield return "GcpDeploy:ProjectId is required.";

        if (string.IsNullOrWhiteSpace(Region))
            yield return "GcpDeploy:Region is required.";

        if (string.IsNullOrWhiteSpace(HostPublicAddress))
            yield return
                "GcpDeploy:HostPublicAddress is required so Cloud Run workers can reach local services.";

        if (string.IsNullOrWhiteSpace(RabbitMqPublicUrl))
            yield return "GcpDeploy:RabbitMqPublicUrl is required (workers connect via MassTransit).";

        if (string.Equals(ImageTag?.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
            yield return "GcpDeploy:ImageTag must not be 'latest'. Leave it blank to generate a pinned deploy tag.";

        foreach (var worker in WorkerTypeExtensions.All())
        {
            if (!TryGetWorkerProjectPath(worker, out var projectPath))
            {
                yield return $"GcpDeploy:WorkerProjectPaths:{worker} is required.";
                continue;
            }

            if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                yield return $"GcpDeploy:WorkerProjectPaths:{worker} must point to a .csproj file.";
        }
    }

    private bool TryGetWorkerProjectPath(WorkerType worker, out string projectPath)
    {
        if ((WorkerProjectPaths.TryGetValue(worker.ToString(), out var configured) ||
             WorkerProjectPaths.TryGetValue(worker.ToSlug(), out configured)) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            projectPath = configured.Replace('\\', '/').Trim();
            return true;
        }

        projectPath = string.Empty;
        return false;
    }

    private static class ImageTagDefaults
    {
        public static string Create(string repoRoot)
        {
            var version = ReadVersion(repoRoot);
            var sha = ReadGitShortSha(repoRoot)
                ?? ReadEnvironmentSha()
                ?? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            return Sanitize($"{version}-{sha}");
        }

        public static string Sanitize(string rawTag)
        {
            var source = string.IsNullOrWhiteSpace(rawTag) ? "local" : rawTag.Trim();
            var chars = source
                .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-' ? ch : '-')
                .ToArray();

            var sanitized = new string(chars).Trim('.', '-');

            if (string.IsNullOrWhiteSpace(sanitized))
                sanitized = "local";

            if (!char.IsLetterOrDigit(sanitized[0]) && sanitized[0] != '_')
                sanitized = $"v{sanitized}";

            if (sanitized.Length > 128)
                sanitized = sanitized[..128].TrimEnd('.', '-');

            return sanitized;
        }

        private static string ReadVersion(string repoRoot)
        {
            try
            {
                var versionFile = Path.Combine(repoRoot, "VERSION");
                return File.Exists(versionFile)
                    ? File.ReadAllText(versionFile).Trim()
                    : "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        private static string? ReadGitShortSha(string repoRoot)
        {
            try
            {
                var startInfo = new ProcessStartInfo("git", "rev-parse --short=12 HEAD")
                {
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    WorkingDirectory = Directory.Exists(repoRoot)
                        ? repoRoot
                        : Directory.GetCurrentDirectory(),
                };

                using var process = Process.Start(startInfo);
                if (process is null)
                    return null;

                if (!process.WaitForExit(milliseconds: 2_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best-effort cleanup only.
                    }

                    return null;
                }

                var sha = process.StandardOutput.ReadToEnd().Trim();
                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(sha)
                    ? sha
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadEnvironmentSha()
        {
            var sha = Environment.GetEnvironmentVariable("GITHUB_SHA")
                ?? Environment.GetEnvironmentVariable("COMMIT_SHA")
                ?? Environment.GetEnvironmentVariable("SOURCE_VERSION");

            return string.IsNullOrWhiteSpace(sha)
                ? null
                : sha.Trim()[..Math.Min(12, sha.Trim().Length)];
        }
    }
}
