using NightmareV2.Application.Workers;
using NightmareV2.Infrastructure.Workers;
using Xunit;

namespace NightmareV2.Workers.Enum.Tests;

public sealed class NormalizationAndParsingTests
{
    [Fact]
    public void ProviderSelection_DefaultsToSubfinderAndAmass()
    {
        var options = new SubdomainEnumerationOptions();
        var providers = SubdomainEnumerationProviderSelection.ResolveEnabledProviders(options);
        Assert.Equal(["subfinder", "amass"], providers);
    }

    [Fact]
    public void NormalizeHostname_RejectsSchemesAndPaths()
    {
        Assert.Null(SubdomainEnumerationNormalization.NormalizeHostname("https://api.example.com"));
        Assert.Null(SubdomainEnumerationNormalization.NormalizeHostname("api.example.com/admin"));
        Assert.Equal("api.example.com", SubdomainEnumerationNormalization.NormalizeHostname("API.EXAMPLE.COM."));
    }

    [Fact]
    public void IsInScope_ValidatesRootBoundaries()
    {
        Assert.True(SubdomainEnumerationNormalization.IsInScope("api.example.com", "example.com"));
        Assert.True(SubdomainEnumerationNormalization.IsInScope("example.com", "example.com"));
        Assert.False(SubdomainEnumerationNormalization.IsInScope("example.com.evil.com", "example.com"));
    }

    [Fact]
    public void SubfinderParser_SupportsJsonLinesAndPlainFallback()
    {
        var output = """
                     {"host":"api.example.com"}
                     {"input":"dev.example.com"}
                     stage.example.com
                     """;
        var parsed = SubdomainEnumerationParsers.ParseSubfinderOutput(output);
        Assert.Contains("api.example.com", parsed);
        Assert.Contains("dev.example.com", parsed);
        Assert.Contains("stage.example.com", parsed);
    }
}
