using ArgusEngine.Contracts;

namespace ArgusEngine.Application.Assets;

public interface IAssetRelationshipValidator
{
    bool IsAllowed(AssetKind parentKind, AssetKind childKind, AssetRelationshipType relationshipType);

    Task<bool> WouldCreateCycleAsync(
        Guid targetId,
        Guid parentAssetId,
        Guid childAssetId,
        CancellationToken cancellationToken = default);
}
