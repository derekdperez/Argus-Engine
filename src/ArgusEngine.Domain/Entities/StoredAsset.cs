using ArgusEngine.Contracts;

namespace ArgusEngine.Domain.Entities;

public class StoredAsset
{
    public Guid Id { get; set; }

    public Guid TargetId { get; set; }

    public ReconTarget? Target { get; set; }

    public AssetKind Kind { get; set; }

    public AssetCategory Category { get; set; } = AssetCategory.Host;

    /// <summary>
    /// Normalized identity key, such as URL without fragment or lower-cased host.
    /// </summary>
    public string CanonicalKey { get; set; } = "";

    public string RawValue { get; set; } = "";

    public string? DisplayName { get; set; }

    public int Depth { get; set; }

    public string DiscoveredBy { get; set; } = "";

    /// <summary>
    /// Human-readable description of how the asset was found.
    /// </summary>
    public string DiscoveryContext { get; set; } = "";

    public DateTimeOffset DiscoveredAtUtc { get; set; }

    public DateTimeOffset? LastSeenAtUtc { get; set; }

    public decimal Confidence { get; set; } = 1.0m;

    public string LifecycleStatus { get; set; } = AssetLifecycleStatus.Queued;

    /// <summary>
    /// Type-specific payload, such as URL fetch request/response metadata.
    /// </summary>
    public string? TypeDetailsJson { get; set; }

    public string? FinalUrl { get; set; }

    public int RedirectCount { get; set; }

    public string? RedirectChainJson { get; set; }
}