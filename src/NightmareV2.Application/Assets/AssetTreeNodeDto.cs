namespace NightmareV2.Application.Assets;

public sealed record AssetTreeNodeDto(
    AssetNodeDto Asset,
    AssetRelationshipDto? IncomingRelationship,
    int GraphDepth,
    IReadOnlyList<AssetTreeNodeDto> Children);
