using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Application.Assets;

public interface IAssetGraphService
{
    Task<AssetUpsertResult> UpsertAssetAsync(
        AssetDiscovered message,
        CanonicalAsset canonical,
        CancellationToken cancellationToken = default);

    Task<AssetRelationshipResult> UpsertRelationshipAsync(
        AssetRelationshipDiscovered message,
        CancellationToken cancellationToken = default);

    Task<AssetNodeDto?> GetRootAssetAsync(
        Guid targetId,
        CancellationToken cancellationToken = default);

    Task<AssetNodeDto?> GetAssetAsync(
        Guid targetId,
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetNodeDto>> GetChildrenAsync(
        Guid targetId,
        Guid parentAssetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetNodeDto>> GetParentsAsync(
        Guid targetId,
        Guid childAssetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetNodeDto>> GetAncestorsAsync(
        Guid targetId,
        Guid childAssetId,
        int maxDepth,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetTreeNodeDto>> GetDescendantsAsync(
        Guid targetId,
        Guid rootAssetId,
        int maxDepth,
        CancellationToken cancellationToken = default);
}
