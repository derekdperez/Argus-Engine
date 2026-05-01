namespace NightmareV2.CommandCenter.Models;

public sealed record CreateTargetRequest(string RootDomain, int GlobalMaxDepth = 12);

public sealed record UpdateTargetRequest(string RootDomain, int GlobalMaxDepth = 12);

public sealed record UpdateTargetMaxDepthRequest(int GlobalMaxDepth, IReadOnlyList<Guid>? TargetIds = null, bool AllTargets = false);

public sealed record UpdateTargetMaxDepthResult(int Updated, int GlobalMaxDepth);

public sealed record BulkImportRequest(IReadOnlyList<string>? Domains, int GlobalMaxDepth = 12);

public sealed record BulkImportResult(int Created, int SkippedAlreadyExist, int SkippedEmptyOrInvalid, int SkippedDuplicateInBatch);

public sealed record TargetSummary(
    Guid Id,
    string RootDomain,
    int GlobalMaxDepth,
    DateTimeOffset CreatedAtUtc,
    long SubdomainCount = 0,
    long ConfirmedAssetCount = 0,
    long ConfirmedUrlCount = 0,
    long QueuedAssetCount = 0,
    DateTimeOffset? LastRunAtUtc = null);
