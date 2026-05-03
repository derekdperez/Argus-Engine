using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NightmareV2.CommandCenter.Security;
using Xunit;

namespace NightmareV2.CommandCenter.Tests;

public sealed class SensitiveEndpointProtectionTests
{
    [Theory]
    [InlineData("/api/diagnostics/self", SensitiveEndpointKind.Diagnostics)]
    [InlineData("/api/diagnostics/dependencies", SensitiveEndpointKind.Diagnostics)]
    [InlineData("/api/maintenance/clear-all-assets", SensitiveEndpointKind.Maintenance)]
    [InlineData("/api/maintenance/status", SensitiveEndpointKind.Maintenance)]
    public void Classify_ProtectsSensitiveEndpointFamilies(string path, SensitiveEndpointKind expected)
    {
        Assert.Equal(expected, SensitiveEndpointProtection.Classify(new PathString(path)));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/api/targets")]
    [InlineData("/hubs/discovery")]
    public void Classify_DoesNotProtectPublicOrNormalApplicationEndpoints(string path)
    {
        Assert.Null(SensitiveEndpointProtection.Classify(new PathString(path)));
    }

    [Fact]
    public void IsAuthorized_UsesExactApiKeyMatch()
    {
        Assert.True(SensitiveEndpointProtection.IsAuthorized("prod-secret", "prod-secret"));
        Assert.True(SensitiveEndpointProtection.IsAuthorized(" prod-secret ", "prod-secret"));

        Assert.False(SensitiveEndpointProtection.IsAuthorized("prod-secret-2", "prod-secret"));
        Assert.False(SensitiveEndpointProtection.IsAuthorized("", "prod-secret"));
        Assert.False(SensitiveEndpointProtection.IsAuthorized("prod-secret", ""));
    }

    [Fact]
    public void Policy_PrefersArgusKeysAndFallsBackToNightmareKeys()
    {
        var config = new ConfigurationManager();
        config["Nightmare:Diagnostics:Enabled"] = "false";
        config["Nightmare:Diagnostics:ApiKey"] = "old";
        config["Argus:Diagnostics:Enabled"] = "true";
        config["Argus:Diagnostics:ApiKey"] = "new";
        config["Argus:Diagnostics:RateLimit:PermitLimit"] = "7";
        config["Argus:Diagnostics:RateLimit:WindowSeconds"] = "9";

        var policy = SensitiveEndpointPolicy.FromConfiguration(config, SensitiveEndpointKind.Diagnostics);

        Assert.True(policy.Enabled);
        Assert.Equal("new", policy.ApiKey);
        Assert.Equal(7, policy.RateLimitPermitLimit);
        Assert.Equal(9, policy.RateLimitWindowSeconds);
    }

    [Fact]
    public void Policy_FallsBackToNightmareKeysDuringTransition()
    {
        var config = new ConfigurationManager();
        config["Nightmare:DataMaintenance:Enabled"] = "true";
        config["Nightmare:DataMaintenance:ApiKey"] = "legacy";
        config["Nightmare:DataMaintenance:RateLimit:PermitLimit"] = "3";
        config["Nightmare:DataMaintenance:RateLimit:WindowSeconds"] = "11";

        var policy = SensitiveEndpointPolicy.FromConfiguration(config, SensitiveEndpointKind.Maintenance);

        Assert.True(policy.Enabled);
        Assert.Equal("legacy", policy.ApiKey);
        Assert.Equal(3, policy.RateLimitPermitLimit);
        Assert.Equal(11, policy.RateLimitWindowSeconds);
    }

    [Fact]
    public void RateLimiter_DeniesRequestsOverTheConfiguredWindowAndThenResets()
    {
        var limiter = new SensitiveEndpointRateLimiter();
        var now = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(limiter.TryAcquire("diagnostics:client", 2, TimeSpan.FromSeconds(60), now, out var retry1));
        Assert.Equal(TimeSpan.Zero, retry1);

        Assert.True(limiter.TryAcquire("diagnostics:client", 2, TimeSpan.FromSeconds(60), now.AddSeconds(1), out var retry2));
        Assert.Equal(TimeSpan.Zero, retry2);

        Assert.False(limiter.TryAcquire("diagnostics:client", 2, TimeSpan.FromSeconds(60), now.AddSeconds(2), out var retry3));
        Assert.True(retry3 > TimeSpan.Zero);

        Assert.True(limiter.TryAcquire("diagnostics:client", 2, TimeSpan.FromSeconds(60), now.AddSeconds(61), out var retry4));
        Assert.Equal(TimeSpan.Zero, retry4);
    }
}
