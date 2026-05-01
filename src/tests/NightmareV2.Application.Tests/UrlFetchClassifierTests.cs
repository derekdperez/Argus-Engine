using NightmareV2.Application.Assets;
using Xunit;

namespace NightmareV2.Application.Tests;

public sealed class UrlFetchClassifierTests
{
    [Theory]
    [InlineData(@"{""error_code"":404,""message"":""missing""}", "application/json")]
    [InlineData(@"{""status"":""404"",""detail"":""no route""}", "application/problem+json")]
    [InlineData(@"{""errors"":[{""type"":""route_not_found""}]}", "application/json")]
    [InlineData(@"[{""metadata"":{""statusCode"":404}}]", null)]
    public void LooksLikeSoft404_DetectsStructuredJson404Signals(string body, string? contentType)
    {
        Assert.True(UrlFetchClassifier.LooksLikeSoft404(Snapshot(body, contentType)));
    }

    [Fact]
    public void LooksLikeSoft404_IgnoresSuccessfulJsonAndInvalidJson()
    {
        Assert.False(UrlFetchClassifier.LooksLikeSoft404(Snapshot(@"{""status"":200,""message"":""ok""}", "application/json")));
        Assert.False(UrlFetchClassifier.LooksLikeSoft404(Snapshot("{this is not json", "application/json")));
        Assert.False(UrlFetchClassifier.LooksLikeSoft404(Snapshot("", "application/json")));
    }

    private static UrlFetchSnapshot Snapshot(string? body, string? contentType) =>
        new(
            "GET",
            new Dictionary<string, string>(),
            null,
            200,
            new Dictionary<string, string>(),
            body,
            body?.Length,
            12.5,
            contentType,
            DateTimeOffset.UtcNow);
}
