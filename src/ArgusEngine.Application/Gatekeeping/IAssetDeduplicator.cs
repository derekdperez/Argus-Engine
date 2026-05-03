namespace ArgusEngine.Application.Gatekeeping;

/// <summary>
/// Atomic check-and-set in Redis (design §2). Production may layer Bloom filters in front of NX keys.
/// </summary>
public interface IAssetDeduplicator
{
    /// <summary>Returns true if this is the first time the canonical key is seen for the target.</summary>
    Task<bool> TryReserveAsync(Guid targetId, string canonicalKey, CancellationToken cancellationToken = default);

    Task ReleaseAsync(Guid targetId, string canonicalKey, CancellationToken cancellationToken = default);

    /// <summary>Remove dedupe keys for one recon target (e.g. after clearing that target's assets from Postgres).</summary>
    Task ClearForTargetAsync(Guid targetId, CancellationToken cancellationToken = default);

    /// <summary>Remove all asset dedupe keys (e.g. after clearing targets or all assets).</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
