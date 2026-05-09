using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Gatekeeping;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class ObservabilityStackTests
{
    [Fact]
    public void Canonicalizer_UsesDeterministicHashFallbackForMalformedStructuredAssets()
    {
        var canonicalizer = new DefaultAssetCanonicalizer();
        var first = canonicalizer.Canonicalize(CreateDiscovery(AssetKind.Url, "  not a uri with spaces  "));
        var second = canonicalizer.Canonicalize(CreateDiscovery(AssetKind.Url, "  not a uri with spaces  "));

        Assert.Equal(AssetKind.Url, first.Kind);
        Assert.StartsWith("url:", first.CanonicalKey, StringComparison.Ordinal);
        Assert.Equal(first.CanonicalKey, second.CanonicalKey);
        Assert.Equal("not a uri with spaces", first.NormalizedDisplay);
    }

    private static AssetDiscovered CreateDiscovery(AssetKind kind, string rawValue) =>
        new(
            TargetId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TargetRootDomain: "example.com",
            GlobalMaxDepth: 4,
            Depth: 0,
            Kind: kind,
            RawValue: rawValue,
            DiscoveredBy: "unit-test",
            OccurredAt: DateTimeOffset.UnixEpoch,
            CorrelationId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            AdmissionStage: (AssetAdmissionStage)0,
            AssetId: null);
}
