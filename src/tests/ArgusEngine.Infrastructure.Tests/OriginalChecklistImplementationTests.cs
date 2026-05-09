using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Gatekeeping;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class OriginalChecklistImplementationTests
{
    [Theory]
    [InlineData(AssetKind.Domain, " Example.COM. ", "host:example.com")]
    [InlineData(AssetKind.Subdomain, " API.Example.COM. ", "host:api.example.com")]
    public void Canonicalizer_NormalizesHostLikeAssetsToStableHostKeys(
        AssetKind kind,
        string rawValue,
        string expectedKey)
    {
        var canonicalizer = new DefaultAssetCanonicalizer();

        var canonical = canonicalizer.Canonicalize(CreateDiscovery(kind, rawValue));

        Assert.Equal(AssetKind.Subdomain, canonical.Kind);
        Assert.Equal(expectedKey, canonical.CanonicalKey);
        Assert.Equal(expectedKey["host:".Length..], canonical.NormalizedDisplay);
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
