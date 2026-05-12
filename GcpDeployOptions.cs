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
    public string Region    { get; set; } = "us-central1";

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

    /// <summary>Docker tag applied to all built images.</summary>
    public string ImageTag { get; set; } = "latest";

    // ── Worker scaling defaults ───────────────────────────────────────────────
    public int  WorkerMinInstances  { get; set; } = 0;   // 0 = scale to zero
    public int  WorkerMaxInstances  { get; set; } = 10;
    public int  WorkerConcurrency   { get; set; } = 4;
    public string WorkerCpu         { get; set; } = "1";
    public string WorkerMemory      { get; set; } = "512Mi";

    // ── Connectivity: how Cloud Run workers reach the local host ─────────────
    /// <summary>
    /// Public IP or hostname of the machine running the core services.
    /// Workers on Cloud Run need this to reach RabbitMQ, Postgres, Redis.
    /// </summary>
    public string HostPublicAddress { get; set; } = string.Empty;

    public string RabbitMqPublicUrl  { get; set; } = string.Empty;
    public string PostgresPublicUrl  { get; set; } = string.Empty;
    public string RedisPublicUrl     { get; set; } = string.Empty;

    /// <summary>Base URL of the CommandCenter API, reachable from Cloud Run.</summary>
    public string CommandCenterApiUrl { get; set; } = string.Empty;

    // ── Local core compose ────────────────────────────────────────────────────
    /// <summary>
    /// Path to the docker-compose file for local core services.
    /// Defaults to "deploy/docker-compose.core.yml" relative to RepoRoot.
    /// </summary>
    public string CoreComposeFile { get; set; } = "deploy/docker-compose.core.yml";

    /// <summary>
    /// Absolute path to the repository root.  Required for building images and
    /// locating compose files.  Set automatically by DeployUi; override if
    /// running from a non-root working directory.
    /// </summary>
    public string RepoRoot { get; set; } = Directory.GetCurrentDirectory();

    // ── Validation ────────────────────────────────────────────────────────────
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectId))
            yield return "GcpDeploy:ProjectId is required.";
        if (string.IsNullOrWhiteSpace(Region))
            yield return "GcpDeploy:Region is required.";
        if (string.IsNullOrWhiteSpace(HostPublicAddress))
            yield return "GcpDeploy:HostPublicAddress is required so Cloud Run workers can reach local services.";
        if (string.IsNullOrWhiteSpace(RabbitMqPublicUrl))
            yield return "GcpDeploy:RabbitMqPublicUrl is required (workers connect via MassTransit).";
    }
}
