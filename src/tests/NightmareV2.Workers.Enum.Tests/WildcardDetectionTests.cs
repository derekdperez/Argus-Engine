using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NightmareV2.Application.Workers;
using NightmareV2.Infrastructure.Workers;
using Xunit;

namespace NightmareV2.Workers.Enum.Tests;

public sealed class WildcardDetectionTests
{
    [Fact]
    public async Task DetectWildcardDnsAsync_ReturnsTrueWhenRandomHostsResolveSameSet()
    {
        var resolver = new FakeResolver(["1.1.1.1"]);
        var provider = new AmassEnumerationProvider(
            Options.Create(new SubdomainEnumerationOptions()),
            new ToolProcessRunner(Mock.Of<ILogger<ToolProcessRunner>>()),
            resolver,
            Mock.Of<ILogger<AmassEnumerationProvider>>());

        var wildcard = await provider.DetectWildcardDnsAsync("example.com", CancellationToken.None);
        Assert.True(wildcard);
    }

    [Fact]
    public async Task DetectWildcardDnsAsync_ReturnsFalseWhenResolutionFails()
    {
        var resolver = new ThrowingResolver();
        var provider = new AmassEnumerationProvider(
            Options.Create(new SubdomainEnumerationOptions()),
            new ToolProcessRunner(Mock.Of<ILogger<ToolProcessRunner>>()),
            resolver,
            Mock.Of<ILogger<AmassEnumerationProvider>>());

        var wildcard = await provider.DetectWildcardDnsAsync("example.com", CancellationToken.None);
        Assert.False(wildcard);
    }

    private sealed class FakeResolver(IReadOnlyCollection<string> addresses) : IHostResolver
    {
        public Task<IReadOnlyCollection<string>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default) =>
            Task.FromResult(addresses);
    }

    private sealed class ThrowingResolver : IHostResolver
    {
        public Task<IReadOnlyCollection<string>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("dns unavailable");
    }
}
