using NightmareV2.Workers.Spider;
using Xunit;

namespace NightmareV2.Workers.Spider.Tests;

public sealed class ApiRouteInferenceTests
{
    [Fact]
    public void TryInferEndpoint_TemplatesNumericAndUuidSegmentsAndNormalizesAuthority()
    {
        var uri = new Uri("HTTPS://Api.Example.COM:8443/v1/users/123/orders/550e8400-e29b-41d4-a716-446655440000?expand=true");

        var inferred = ApiRouteInference.TryInferEndpoint(uri, out var endpoint);

        Assert.True(inferred);
        Assert.Equal("https://api.example.com:8443/v1/users/{id}/orders/{uuid}", endpoint);
    }

    [Theory]
    [InlineData("https://example.com/api/users")]
    [InlineData("https://example.com/api")]
    [InlineData("https://example.com/v2/users")]
    [InlineData("https://example.com/graphql")]
    [InlineData("https://example.com/rest/users")]
    public void TryInferEndpoint_DetectsCommonApiShapes(string url)
    {
        Assert.True(ApiRouteInference.TryInferEndpoint(new Uri(url), out var endpoint));
        Assert.StartsWith("https://example.com/", endpoint);
    }

    [Fact]
    public void TryInferEndpoint_ReturnsFalseForNonApiPaths()
    {
        Assert.False(ApiRouteInference.TryInferEndpoint(new Uri("https://example.com/blog/123"), out var endpoint));
        Assert.Equal("", endpoint);
    }

    [Fact]
    public void QueryParameterNames_DecodesDistinctCaseInsensitiveNamesAndIgnoresBlankEntries()
    {
        var uri = new Uri("https://example.com/api/search?q=test&Q=again&empty&encoded%20name=value&&=");

        var names = ApiRouteInference.QueryParameterNames(uri);

        Assert.Equal(["q", "empty", "encoded name"], names);
    }
}
