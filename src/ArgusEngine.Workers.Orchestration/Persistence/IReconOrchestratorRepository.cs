using ArgusEngine.Workers.Orchestration.Configuration;
using ArgusEngine.Workers.Orchestration.State;

namespace ArgusEngine.Workers.Orchestration.Persistence;

public interface IReconOrchestratorRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconTargetSnapshot>> ListTargetsAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconTargetSnapshot>> ResolveTargetsAsync(
        IReadOnlyCollection<Guid> targetIds,
        CancellationToken cancellationToken);

    Task<bool> TryAcquireLeaseAsync(
        Guid targetId,
        Guid instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken);

    Task RenewLeaseAsync(
        Guid targetId,
        Guid instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken);

    Task<ReconOrchestratorState> LoadOrCreateStateAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorOptions options,
        string serializedConfiguration,
        CancellationToken cancellationToken);

    Task SaveStateAsync(
        Guid targetId,
        Guid instanceId,
        ReconOrchestratorState state,
        string serializedConfiguration,
        CancellationToken cancellationToken);

    Task<ProviderRunSnapshot?> GetProviderRunAsync(
        Guid targetId,
        string provider,
        CancellationToken cancellationToken);

    Task StartProviderRunAsync(
        Guid targetId,
        string provider,
        Guid eventId,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListSubdomainsAsync(
        Guid targetId,
        string rootDomain,
        CancellationToken cancellationToken);

    Task<SubdomainUrlProgress> GetSubdomainUrlProgressAsync(
        Guid targetId,
        string subdomain,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingUrlAsset>> ListPendingUrlAssetsAsync(
        Guid targetId,
        string subdomain,
        int limit,
        CancellationToken cancellationToken);

    Task UpsertSubdomainStatusAsync(
        Guid targetId,
        string subdomain,
        SubdomainUrlProgress progress,
        string spiderStatus,
        CancellationToken cancellationToken);

    Task SaveProfileAssignmentAsync(
        Guid targetId,
        string subdomain,
        string machineIdentity,
        string profileId,
        string profileJson,
        string headerOrderJson,
        CancellationToken cancellationToken);
}
