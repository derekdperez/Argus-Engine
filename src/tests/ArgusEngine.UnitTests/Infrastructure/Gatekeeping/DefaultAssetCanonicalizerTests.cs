using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Gatekeeping;
using Xunit;

namespace ArgusEngine.UnitTests.Infrastructure.Gatekeeping;

public sealed class DefaultAssetCanonicalizerTests
{
    private readonly DefaultAssetCanonicalizer _canonicalizer = new();

    [Theory]
    [InlineData(AssetKind.Domain, " API.Example.COM. ", "host:api.example.com", "api.example.com")]
    [InlineData(AssetKind.Subdomain, " WWW.Example.COM. ", "host:www.example.com", "www.example.com")]
    public void Canonicalize_NormalizesHostAssets(AssetKind kind, string rawValue, string expectedKey, string expectedDisplay)
    {
        var canonical = _canonicalizer.Canonicalize(CreateDiscovery(kind, rawValue));

        Assert.Equal(AssetKind.Subdomain, canonical.Kind);
        Assert.Equal(expectedKey, canonical.CanonicalKey);
        Assert.Equal(expectedDisplay, canonical.NormalizedDisplay);
    }

    [Fact]
    public void Canonicalize_TargetsTrimAndLowercaseTheRawTarget()
    {
        var canonical = _canonicalizer.Canonicalize(CreateDiscovery(AssetKind.Target, " Example.COM "));

        Assert.Equal(AssetKind.Target, canonical.Kind);
        Assert.Equal("target:example.com", canonical.CanonicalKey);
        Assert.Equal("example.com", canonical.NormalizedDisplay);
    }

    [Fact]
    public void Canonicalize_NormalizesUrlPathsIdsGuidsAndSortedQueryParameters()
    {
        var rawUrl = "HTTPS://Example.COM:443/Users/123/Files/550e8400-e29b-41d4-a716-446655440000/?B=2&a=1";

        var canonical = _canonicalizer.Canonicalize(CreateDiscovery(AssetKind.Url, rawUrl));

        Assert.Equal(AssetKind.Url, canonical.Kind);
        Assert.Equal("url:https://example.com/users/{id}/files/{guid}/?a=1&b=2", canonical.CanonicalKey);
    }

    [Theory]
    [InlineData(AssetKind.ApiEndpoint, "Example.COM/Products/42?B=2&A=1", "url:https://example.com/products/{id}?a=1&b=2")]
    [InlineData(AssetKind.JavaScriptFile, "http://Example.COM:8080/Assets/App.js", "url:http://example.com:8080/assets/app.js")]
    public void Canonicalize_UsesUrlKeyNamespaceForStructuredAssets(AssetKind kind, string rawValue, string expectedKey)
    {
        var canonical = _canonicalizer.Canonicalize(CreateDiscovery(kind, rawValue));

        Assert.Equal(kind, canonical.Kind);
        Assert.Equal(expectedKey, canonical.CanonicalKey);
    }

    [Fact]
    public void Canonicalize_FallsBackToStableHashWhenStructuredUrlCannotBeParsed()
    {
        var canonical = _canonicalizer.Canonicalize(CreateDiscovery(AssetKind.ApiEndpoint, "not really a url ???"));

        Assert.Equal(AssetKind.ApiEndpoint, canonical.Kind);
        Assert.StartsWith("apiendpoint:", canonical.CanonicalKey, StringComparison.Ordinal);
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
