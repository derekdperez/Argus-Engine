namespace ArgusEngine.Workers.Orchestration.Persistence;

public sealed record ReconTargetSnapshot(Guid Id, string RootDomain, int GlobalMaxDepth);

public sealed record ProviderRunSnapshot(
    Guid TargetId,
    string Provider,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? LastRequestedEventId,
    Guid CorrelationId,
    string? Error);

public sealed record SubdomainUrlProgress(
    string Subdomain,
    int TotalUrlAssets,
    int PendingUrlAssets,
    int ConfirmedUrlAssets);

public sealed record PendingUrlAsset(
    Guid AssetId,
    string Url,
    int Depth);
