using NightmareV2.Contracts;

namespace NightmareV2.Application.Assets;

public sealed record AssetRelationshipDto(
    Guid Id,
    Guid TargetId,
    Guid ParentAssetId,
    Guid ChildAssetId,
    AssetRelationshipType RelationshipType,
    bool IsPrimary,
    decimal Confidence,
    string DiscoveredBy,
    string DiscoveryContext,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc);
