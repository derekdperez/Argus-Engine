namespace ArgusEngine.Application.Orchestration;

public interface IReconOrchestrator
{
    Task<ReconOrchestratorSnapshot> AttachToTargetAsync(
        Guid targetId,
        string attachedBy,
        ReconOrchestratorConfiguration? configuration = null,
        CancellationToken cancellationToken = default);

    Task<ReconOrchestratorTickResult> TickTargetAsync(
        Guid targetId,
        string tickOwner,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetActiveTargetIdsAsync(CancellationToken cancellationToken = default);

    Task<ReconOrchestratorSnapshot?> GetSnapshotAsync(
        Guid targetId,
        CancellationToken cancellationToken = default);
}

public sealed record ReconOrchestratorSnapshot(
    Guid TargetId,
    string RootDomain,
    string Status,
    ReconOrchestratorConfiguration Configuration,
    DateTimeOffset AttachedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReconOrchestratorTickResult(
    Guid TargetId,
    bool Claimed,
    int ProvidersQueued,
    int SubdomainsChecked,
    int SubdomainSeedsQueued,
    int IncompleteSubdomains,
    bool Completed);
