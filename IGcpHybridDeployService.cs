namespace ArgusEngine.CloudDeploy;

/// <summary>
/// Manages the GCP hybrid deployment: core services on the local host,
/// worker services on Google Cloud Run.
/// </summary>
public interface IGcpHybridDeployService
{
    // ── Image building ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds Docker images for the specified workers and pushes them to
    /// Google Artifact Registry.  Reports progress via <paramref name="progress"/>.
    /// </summary>
    Task<BulkDeployResult> BuildAndPushImagesAsync(
        IEnumerable<WorkerType>?          workers  = null,
        IProgress<DeployProgressEvent>?   progress = null,
        CancellationToken                 ct       = default
    );

    // ── Cloud Run worker management ───────────────────────────────────────────

    /// <summary>
    /// Deploys (or re-deploys) all workers to Cloud Run using the current image tag.
    /// </summary>
    Task<BulkDeployResult> DeployWorkersAsync(
        IEnumerable<WorkerType>?          workers  = null,
        IProgress<DeployProgressEvent>?   progress = null,
        CancellationToken                 ct       = default
    );

    /// <summary>Deploys a single worker to Cloud Run.</summary>
    Task<CloudDeployResult> DeployWorkerAsync(
        WorkerType                        worker,
        IProgress<DeployProgressEvent>?   progress = null,
        CancellationToken                 ct       = default
    );

    /// <summary>
    /// Adjusts min/max instance counts for one or all workers without a full
    /// redeploy.
    /// </summary>
    Task<BulkDeployResult> ScaleWorkersAsync(
        int                               minInstances,
        int                               maxInstances,
        IEnumerable<WorkerType>?          workers  = null,
        CancellationToken                 ct       = default
    );

    /// <summary>Returns the live Cloud Run status of all (or the given) workers.</summary>
    Task<IReadOnlyList<WorkerStatus>> GetWorkerStatusesAsync(
        IEnumerable<WorkerType>?          workers  = null,
        CancellationToken                 ct       = default
    );

    /// <summary>Deletes all worker Cloud Run services.</summary>
    Task<BulkDeployResult> TeardownWorkersAsync(
        IEnumerable<WorkerType>?          workers  = null,
        IProgress<DeployProgressEvent>?   progress = null,
        CancellationToken                 ct       = default
    );

    // ── Local core services ───────────────────────────────────────────────────

    /// <summary>
    /// Starts the core services (Postgres, Redis, RabbitMQ, CommandCenter stack)
    /// locally via docker-compose.
    /// </summary>
    Task<CloudDeployResult> StartLocalCoreAsync(
        IProgress<DeployProgressEvent>?   progress = null,
        CancellationToken                 ct       = default
    );

    /// <summary>Stops the local core services docker-compose stack.</summary>
    Task<CloudDeployResult> StopLocalCoreAsync(
        IProgress<DeployProgressEvent>?   progress = null,
        CancellationToken                 ct       = default
    );

    // ── Preflight ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs preflight checks (gcloud auth, Docker daemon, required APIs enabled,
    /// host connectivity) and returns a list of any issues found.
    /// </summary>
    Task<IReadOnlyList<string>> RunPreflightAsync(CancellationToken ct = default);
}
