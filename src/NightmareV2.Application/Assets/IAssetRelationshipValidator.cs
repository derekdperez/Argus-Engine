using NightmareV2.Contracts;

namespace NightmareV2.Application.Assets;

public interface IAssetRelationshipValidator
{
    bool IsAllowed(AssetKind parentKind, AssetKind childKind, AssetRelationshipType relationshipType);

    Task<bool> WouldCreateCycleAsync(
        Guid targetId,
        Guid parentAssetId,
        Guid childAssetId,
        CancellationToken cancellationToken = default);
}
