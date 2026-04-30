namespace NightmareV2.Application.Assets;

public sealed record AssetUpsertResult(
    Guid AssetId,
    bool Inserted,
    bool RelationshipInserted,
    bool RelationshipUpdated,
    string? SkippedReason = null);
