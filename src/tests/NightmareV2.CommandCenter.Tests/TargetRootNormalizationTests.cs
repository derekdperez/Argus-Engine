using NightmareV2.CommandCenter;
using Xunit;

namespace NightmareV2.CommandCenter.Tests;

public sealed class TargetRootNormalizationTests
{
    [Theory]
    [InlineData(" Example.COM. ", "example.com")]
    [InlineData("*.Example.COM", "example.com")]
    [InlineData("https://www.Example.COM/path?q=1", "www.example.com")]
    [InlineData("api.example.com:8443/admin", "api.example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    public void TryNormalize_AcceptsCommonOperatorInputsAndReturnsDnsRoot(string input, string expected)
    {
        var ok = TargetRootNormalization.TryNormalize(input, out var root);

        Assert.True(ok);
        Assert.Equal(expected, root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("exa mple.com")]
    [InlineData("example..com")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("*.")]
    [InlineData("https://")]
    public void TryNormalize_RejectsInvalidOrAmbiguousRoots(string input)
    {
        var ok = TargetRootNormalization.TryNormalize(input, out var root);

        Assert.False(ok);
        Assert.Equal("", root);
    }

    [Fact]
    public void TryNormalize_RejectsOverlongLabelsAndDomains()
    {
        var overlongLabel = new string('a', 64) + ".example.com";
        var overlongDomain = string.Join('.', Enumerable.Repeat("abcdefghi", 30)) + ".com";

        Assert.False(TargetRootNormalization.TryNormalize(overlongLabel, out _));
        Assert.False(TargetRootNormalization.TryNormalize(overlongDomain, out _));
    }

    [Fact]
    public void SplitLines_PreservesEmptyLinesSoBulkImportCanCountSkippedEmptyRows()
    {
        var lines = TargetRootNormalization.SplitLines("one\r\ntwo\n\nthree\rfour").ToArray();

        Assert.Equal(["one", "two", "", "three", "four"], lines);
    }
}
