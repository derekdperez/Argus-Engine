namespace ArgusEngine.CloudDeploy;

public enum CloudDeployStatus
{
    Unknown,
    NotDeployed,
    Deploying,
    Running,
    Scaling,
    Failed,
}

/// <summary>Live status of a single worker deployment on Cloud Run.</summary>
public sealed record WorkerStatus(
    WorkerType Worker,
    CloudDeployStatus Status,
    string? ServiceUrl,
    int     CurrentInstances,
    int     MinInstances,
    int     MaxInstances,
    string? ImageUri,
    string? LastError
);

/// <summary>Result of a single deploy/scale/teardown operation.</summary>
public sealed record CloudDeployResult(
    bool    Success,
    string  Message,
    string? ServiceUrl = null
)
{
    public static CloudDeployResult Ok(string message, string? url = null) =>
        new(true, message, url);

    public static CloudDeployResult Fail(string message) =>
        new(false, message);
}

/// <summary>Aggregated result across all workers.</summary>
public sealed record BulkDeployResult(
    IReadOnlyList<(WorkerType Worker, CloudDeployResult Result)> Results
)
{
    public bool AllSucceeded => Results.All(r => r.Result.Success);
    public IEnumerable<(WorkerType Worker, CloudDeployResult Result)> Failures =>
        Results.Where(r => !r.Result.Success);
}

/// <summary>Progress event emitted during long-running operations.</summary>
public sealed record DeployProgressEvent(
    WorkerType? Worker,
    string      Message,
    bool        IsError = false
);
