namespace ArgusEngine.Workers.Orchestration.State;

public sealed class ReconOrchestratorState
{
    public int Version { get; set; } = 1;

    public Guid TargetId { get; set; }

    public string RootDomain { get; set; } = string.Empty;

    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, ProviderRunState> ProviderRuns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, SubdomainReconState> Subdomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ReconWorkerProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderRunState
{
    public string Provider { get; set; } = string.Empty;

    public string Status { get; set; } = "NotStarted";

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public Guid? LastRequestedEventId { get; set; }

    public string? Error { get; set; }
}

public sealed class SubdomainReconState
{
    public string Subdomain { get; set; } = string.Empty;

    public string SpiderStatus { get; set; } = "Unknown";

    public int TotalUrlAssets { get; set; }

    public int PendingUrlAssets { get; set; }

    public int ConfirmedUrlAssets { get; set; }

    public DateTimeOffset LastCheckedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<Guid> ResumeAssetIds { get; set; } = [];
}
