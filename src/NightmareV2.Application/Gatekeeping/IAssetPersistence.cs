using NightmareV2.Application.Assets;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Application.Gatekeeping;

public interface IAssetPersistence
{
    /// <summary>Persists a new asset when unique for (target, canonical key). Returns whether a row was inserted. If the recon target no longer exists (cleared DB, in-flight messages), returns (default, false) so the gatekeeper can release dedupe and ack without faulting.</summary>
    Task<(Guid AssetId, bool Inserted)> PersistNewAssetAsync(
        AssetDiscovered message,
        CanonicalAsset canonical,
        CancellationToken cancellationToken = default);

    Task ConfirmUrlAssetAsync(
        Guid assetId,
        UrlFetchSnapshot snapshot,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}
