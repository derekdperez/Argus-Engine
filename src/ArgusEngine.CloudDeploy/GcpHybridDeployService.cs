using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CloudDeploy;

/// <inheritdoc cref="IGcpHybridDeployService"/>
internal sealed class GcpHybridDeployService(
    IOptions<GcpDeployOptions>      options,
    GcpImageBuilder                 imageBuilder,
    CloudRunWorkerManager           cloudRunManager,
    LocalCoreOrchestrator           localCore,
    ILogger<GcpHybridDeployService> logger)
    : IGcpHybridDeployService
{
    private readonly GcpDeployOptions _opts = options.Value;

    // ── Image building ────────────────────────────────────────────────────────

    public async Task<BulkDeployResult> BuildAndPushImagesAsync(
        IEnumerable<WorkerType>?          workers,
        IProgress<DeployProgressEvent>?   progress,
        CancellationToken                 ct)
    {
        var targets = ResolveWorkers(workers);

        // Ensure GAR repository exists first
        await imageBuilder.EnsureGarRepositoryAsync(progress, ct);

        var results = new List<(WorkerType, CloudDeployResult)>();

        foreach (var worker in targets)
        {
            ct.ThrowIfCancellationRequested();

            var buildResult = await imageBuilder.BuildImageAsync(worker, progress, ct);
            if (!buildResult.Success)
            {
                results.Add((worker, buildResult));
                continue;
            }

            var pushResult = await imageBuilder.PushImageAsync(worker, progress, ct);
            results.Add((worker, pushResult));
        }

        return new BulkDeployResult(results);
    }

    // ── Worker deployment ─────────────────────────────────────────────────────

    public async Task<BulkDeployResult> DeployWorkersAsync(
        IEnumerable<WorkerType>?          workers,
        IProgress<DeployProgressEvent>?   progress,
        CancellationToken                 ct)
    {
        var targets = ResolveWorkers(workers);
        var results = new List<(WorkerType, CloudDeployResult)>();

        // Deploy concurrently — Cloud Run handles parallel create/update fine
        var tasks = targets.Select(async w =>
        {
            var result = await cloudRunManager.DeployAsync(w, progress, ct);
            return (Worker: w, Result: result);
        });

        foreach (var (worker, result) in await Task.WhenAll(tasks))
        {
            results.Add((worker, result));
            if (!result.Success)
                logger.LogWarning("Deploy failed for {Worker}: {Message}", worker, result.Message);
        }

        return new BulkDeployResult(results);
    }

    public Task<CloudDeployResult> DeployWorkerAsync(
        WorkerType                        worker,
        IProgress<DeployProgressEvent>?   progress,
        CancellationToken                 ct)
        => cloudRunManager.DeployAsync(worker, progress, ct);

    // ── Scaling ───────────────────────────────────────────────────────────────

    public async Task<BulkDeployResult> ScaleWorkersAsync(
        int                               minInstances,
        int                               maxInstances,
        IEnumerable<WorkerType>?          workers,
        CancellationToken                 ct)
    {
        var targets = ResolveWorkers(workers);
        var results = new List<(WorkerType, CloudDeployResult)>();

        var tasks = targets.Select(async w =>
        {
            var result = await cloudRunManager.ScaleAsync(w, minInstances, maxInstances, ct);
            return (Worker: w, Result: result);
        });

        foreach (var (worker, result) in await Task.WhenAll(tasks))
            results.Add((worker, result));

        return new BulkDeployResult(results);
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkerStatus>> GetWorkerStatusesAsync(
        IEnumerable<WorkerType>?          workers,
        CancellationToken                 ct)
    {
        var targets = ResolveWorkers(workers);
        var statuses = new List<WorkerStatus>();

        foreach (var worker in targets)
        {
            try
            {
                statuses.Add(await cloudRunManager.GetStatusAsync(worker, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud status fetch failed for {Worker}", worker);
                statuses.Add(
                    new WorkerStatus(
                        Worker: worker,
                        Status: CloudDeployStatus.Failed,
                        ServiceUrl: null,
                        CurrentInstances: 0,
                        MinInstances: 0,
                        MaxInstances: 0,
                        ImageUri: null,
                        LastError: ex.Message));
            }
        }

        return statuses;
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    public async Task<BulkDeployResult> TeardownWorkersAsync(
        IEnumerable<WorkerType>?          workers,
        IProgress<DeployProgressEvent>?   progress,
        CancellationToken                 ct)
    {
        var targets = ResolveWorkers(workers);
        var results = new List<(WorkerType, CloudDeployResult)>();

        foreach (var worker in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(worker, $"Tearing down {worker}..."));
            var result = await cloudRunManager.TeardownAsync(worker, ct);
            results.Add((worker, result));
        }

        return new BulkDeployResult(results);
    }

    // ── Local core ────────────────────────────────────────────────────────────

    public Task<CloudDeployResult> StartLocalCoreAsync(
        IProgress<DeployProgressEvent>?   progress,
        CancellationToken                 ct)
        => localCore.StartAsync(progress, ct);

    public Task<CloudDeployResult> StopLocalCoreAsync(
        IProgress<DeployProgressEvent>?   progress,
        CancellationToken                 ct)
        => localCore.StopAsync(progress, ct);

    // ── Preflight ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> RunPreflightAsync(CancellationToken ct)
    {
        var issues = new List<string>();

        // Config validation
        issues.AddRange(_opts.Validate());

        // Docker
        if (!await imageBuilder.IsDockerAvailableAsync(ct))
            issues.Add("Docker daemon is not running or not installed.");

        // gcloud auth
        if (!await imageBuilder.IsGcloudAuthenticatedAsync(ct))
            issues.Add("No active gcloud account. Run: gcloud auth login");

        // Connectivity check (best-effort)
        if (!string.IsNullOrWhiteSpace(_opts.HostPublicAddress))
        {
            foreach (var port in new[] { 5672, 5432, 6379 })
            {
                if (!await IsPortReachableAsync(_opts.HostPublicAddress, port, ct))
                    issues.Add($"Port {port} is not reachable on {_opts.HostPublicAddress}. " +
                               "Ensure core services are running and the firewall is open.");
            }
        }

        return issues;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<WorkerType> ResolveWorkers(IEnumerable<WorkerType>? workers) =>
        workers ?? WorkerTypeExtensions.All();

    private static async Task<bool> IsPortReachableAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
