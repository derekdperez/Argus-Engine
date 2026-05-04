using ArgusEngine.Contracts;

namespace ArgusEngine.Domain.Entities;

public class StoredAsset
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public ReconTarget? Target { get; set; }
    public AssetKind Kind { get; set; }
    public AssetCategory Category { get; set; } = AssetCategory.Host;
    /// <summary>Normalized identity key (URL without fragment, lowercased host, etc.).</summary>
    public string CanonicalKey { get; set; } = "";
    public string RawValue { get; set; } = "";
    public string? DisplayName { get; set; }
    public int Depth { get; set; }
    public string DiscoveredBy { get; set; } = "";
    /// <summary>Human-readable description of how the asset was found (parent page, wordlist category, etc.).</summary>
    public string DiscoveryContext { get; set; } = "";
    public DateTimeOffset DiscoveredAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public decimal Confidence { get; set; } = 1.0m;

    /// <summary><see cref="AssetLifecycleStatus"/> values.</summary>
    public string LifecycleStatus { get; set; } = AssetLifecycleStatus.Queued;

    /// <summary>Type-specific payload (URL fetch request/response, timings, etc.).</summary>
    public string? TypeDetailsJson { get; set; }

    /// <summary>Final URL reached after following redirects for URL assets.</summary>
    public string? FinalUrl { get; set; }

    /// <summary>Number of HTTP redirects encountered while fetching the URL asset.</summary>
    public int RedirectCount { get; set; }

    /// <summary>JSON array describing each redirect hop for URL assets.</summary>
    public string? RedirectChainJson { get; set; }
}
