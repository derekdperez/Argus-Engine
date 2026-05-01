using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Infrastructure.Gatekeeping;
using Xunit;

namespace NightmareV2.Infrastructure.Tests;

public sealed class DefaultAssetCanonicalizerTests
{
    private readonly DefaultAssetCanonicalizer _canonicalizer = new();

    [Fact]
    public void Canonicalize_Host_LowercasesTrimsSchemeAndPunycodeNormalizes()
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.Subdomain, " HTTPS://Bücher.Example. "));

        Assert.Equal(AssetKind.Subdomain, canonical.Kind);
        Assert.Equal("xn--bcher-kva.example", canonical.NormalizedDisplay);
        Assert.Equal("host:xn--bcher-kva.example", canonical.CanonicalKey);
    }

    [Fact]
    public void Canonicalize_Url_AddsHttpsWhenMissingAndSortsDecodedQueryParameters()
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.Url, "Example.COM/a/path?b=2&a=hello%20world"));

        Assert.Equal("https://example.com/a/path?a=hello%20world&b=2", canonical.NormalizedDisplay);
        Assert.Equal("url:https://example.com/a/path?a=hello%20world&b=2", canonical.CanonicalKey);
    }

    [Fact]
    public void Canonicalize_ExplicitDefaultPort_IsRemovedForHttpUrls()
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.Url, "https://Example.COM:443/login?z=1&a=2"));

        Assert.Equal("https://example.com/login?a=2&z=1", canonical.NormalizedDisplay);
    }

    [Fact]
    public void Canonicalize_ApiEndpoint_TemplatesIntegerAndUuidSegmentsAndDropsQuery()
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.ApiEndpoint, "https://api.example.com/v1/users/123/orders/550e8400-e29b-41d4-a716-446655440000?token=secret"));

        Assert.Equal("https://api.example.com/v1/users/{id}/orders/{uuid}", canonical.NormalizedDisplay);
        Assert.Equal("api_endpoint:https://api.example.com/v1/users/{id}/orders/{uuid}", canonical.CanonicalKey);
    }

    [Theory]
    [InlineData("POST https://api.example.com/users/42", "POST https://api.example.com/users/{id}")]
    [InlineData("patch|https://api.example.com/users/{id}", "PATCH https://api.example.com/users/{id}")]
    [InlineData("https://api.example.com/users/42", "GET https://api.example.com/users/{id}")]
    public void Canonicalize_ApiMethod_NormalizesMethodAndEndpoint(string raw, string display)
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.ApiMethod, raw));

        Assert.Equal(display, canonical.NormalizedDisplay);
        Assert.StartsWith("api_method:api_endpoint:https://api.example.com/users/{id}:", canonical.CanonicalKey);
    }

    [Fact]
    public void Canonicalize_Parameter_UsesOwnerLocationAndLowercaseName()
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.Parameter, "api_method:https://api.example.com/users/{id}:GET|Query|UserId"));

        Assert.Equal("userid", canonical.NormalizedDisplay);
        Assert.Equal("api_parameter:api_method:https://api.example.com/users/{id}:GET:query:userid", canonical.CanonicalKey);
    }

    [Fact]
    public void Canonicalize_SecretsHashValueButKeepDisplayForOperatorContext()
    {
        var first = _canonicalizer.Canonicalize(Raw(AssetKind.Secret, "aws:AKIA1234567890ABCDEF"));
        var second = _canonicalizer.Canonicalize(Raw(AssetKind.Secret, "aws:AKIA1234567890ABCDEF"));

        Assert.Equal(first.CanonicalKey, second.CanonicalKey);
        Assert.StartsWith("secret:aws:", first.CanonicalKey);
        Assert.DoesNotContain("AKIA1234567890ABCDEF", first.CanonicalKey);
        Assert.Equal("aws:AKIA1234567890ABCDEF", first.NormalizedDisplay);
    }

    [Theory]
    [InlineData("AS15169", "15169")]
    [InlineData(" as  64512 ", "64512")]
    [InlineData("not-an-asn", "not-an-asn")]
    public void Canonicalize_Asn_ExtractsDigitsWhenPresent(string raw, string expected)
    {
        var canonical = _canonicalizer.Canonicalize(Raw(AssetKind.Asn, raw));

        Assert.Equal(expected, canonical.NormalizedDisplay);
        Assert.Equal($"asn:{expected}", canonical.CanonicalKey);
    }

    private static AssetDiscovered Raw(AssetKind kind, string rawValue) =>
        new(
            Guid.NewGuid(),
            "example.com",
            12,
            0,
            kind,
            rawValue,
            "test",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            AssetAdmissionStage.Raw,
            AssetId: null);
}
