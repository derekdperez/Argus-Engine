using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Gatekeeping;
using FluentAssertions;
using Xunit;

namespace ArgusEngine.UnitTests.Infrastructure.Gatekeeping;

public class DefaultAssetCanonicalizerTests
{
    private readonly DefaultAssetCanonicalizer _sut = new();

    [Theory]
    [InlineData("example.com", "host:example.com", "example.com")]
    [InlineData("  EXAMPLE.COM.  ", "host:example.com", "example.com")]
    public void Canonicalize_Subdomain_Works(string raw, string expectedKey, string expectedDisplay)
    {
        var message = new AssetDiscovered(
            TargetId: Guid.NewGuid(),
            TargetRootDomain: "example.com",
            GlobalMaxDepth: 3,
            Depth: 0,
            Kind: AssetKind.Subdomain,
            RawValue: raw,
            DiscoveredBy: "test",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            AdmissionStage: AssetAdmissionStage.Raw,
            AssetId: null);
        
        var result = _sut.Canonicalize(message);

        result.Kind.Should().Be(AssetKind.Subdomain);
        result.CanonicalKey.Should().Be(expectedKey);
        result.NormalizedDisplay.Should().Be(expectedDisplay);
    }

    [Theory]
    [InlineData("https://EXAMPLE.com/Path/", "url:https://example.com/path/")]
    [InlineData("http://example.com:8080/a/b?z=1&a=2", "url:http://example.com:8080/a/b?a=2&z=1")]
    [InlineData("example.com/login", "url:https://example.com/login")]
    public void Canonicalize_Url_NormalizesCorrectly(string raw, string expectedKey)
    {
        var message = new AssetDiscovered(
            TargetId: Guid.NewGuid(),
            TargetRootDomain: "example.com",
            GlobalMaxDepth: 3,
            Depth: 0,
            Kind: AssetKind.Url,
            RawValue: raw,
            DiscoveredBy: "test",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            AdmissionStage: AssetAdmissionStage.Raw,
            AssetId: null);

        var result = _sut.Canonicalize(message);

        result.CanonicalKey.Should().Be(expectedKey);
    }

    [Fact]
    public void Canonicalize_Url_ReplacesIdsAndGuids()
    {
        var guid = Guid.NewGuid().ToString();
        var raw = $"https://api.example.com/v1/users/123/profile/{guid}";
        var message = new AssetDiscovered(
            TargetId: Guid.NewGuid(),
            TargetRootDomain: "example.com",
            GlobalMaxDepth: 3,
            Depth: 0,
            Kind: AssetKind.ApiEndpoint,
            RawValue: raw,
            DiscoveredBy: "test",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            AdmissionStage: AssetAdmissionStage.Raw,
            AssetId: null);

        var result = _sut.Canonicalize(message);

        result.CanonicalKey.Should().Be("url:https://api.example.com/v1/users/{id}/profile/{guid}");
    }

    [Fact]
    public void Canonicalize_UnknownKind_UsesStableHash()
    {
        var message = new AssetDiscovered(
            TargetId: Guid.NewGuid(),
            TargetRootDomain: "example.com",
            GlobalMaxDepth: 3,
            Depth: 0,
            Kind: (AssetKind)999,
            RawValue: "some-secret-value",
            DiscoveredBy: "test",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            AdmissionStage: AssetAdmissionStage.Raw,
            AssetId: null);

        var result = _sut.Canonicalize(message);

        result.CanonicalKey.Should().StartWith("999:");
        result.CanonicalKey.Length.Should().BeGreaterThan(10);
    }
}
