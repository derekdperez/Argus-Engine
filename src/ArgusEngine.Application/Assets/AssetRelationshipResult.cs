namespace ArgusEngine.Application.Assets;

public sealed record AssetRelationshipResult(
    Guid? RelationshipId,
    bool Inserted,
    bool Updated,
    string? RejectedReason = null);
