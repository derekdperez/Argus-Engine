using NightmareV2.Application.Gatekeeping;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Infrastructure.Gatekeeping;
using Xunit;

namespace NightmareV2.Infrastructure.Tests;

public sealed class DnsTargetScopeEvaluatorTests
{
    private readonly DnsTargetScopeEvaluator _scope = new();

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("api.example.com", true)]
    [InlineData("deep.api.example.com", true)]
    [InlineData("example.com.evil.test", false)]
    [InlineData("evil-example.com", false)]
    public void IsInScope_HostKinds_RequireExactRootOrSubdomainBoundary(string host, bool expected)
    {
        var canonical = new CanonicalAsset(AssetKind.Subdomain, $"host:{host}", host);

        Assert.Equal(expected, _scope.IsInScope(Message(AssetKind.Subdomain), canonical));
    }

    [Theory]
    [InlineData("https://example.com/login", true)]
    [InlineData("https://assets.example.com/app.js", true)]
    [InlineData("https://example.com.evil.test/login", false)]
    [InlineData("not a url", false)]
    public void IsInScope_WebAssets_ValidatesUriHostBoundary(string normalizedDisplay, bool expected)
    {
        var canonical = new CanonicalAsset(AssetKind.Url, $"url:{normalizedDisplay}", normalizedDisplay);

        Assert.Equal(expected, _scope.IsInScope(Message(AssetKind.Url), canonical));
    }

    [Fact]
    public void IsInScope_NonDnsAssets_AreAllowedForDownstreamClassifiers()
    {
        var canonical = new CanonicalAsset(AssetKind.Secret, "secret:aws:hash", "aws:secret");

        Assert.True(_scope.IsInScope(Message(AssetKind.Secret), canonical));
    }

    private static AssetDiscovered Message(AssetKind kind) =>
        new(
            Guid.NewGuid(),
            "Example.COM.",
            12,
            1,
            kind,
            "raw",
            "test",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            AssetAdmissionStage.Raw,
            AssetId: null);
}
