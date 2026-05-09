using ArgusEngine.Workers.TechnologyIdentification;
using Xunit;

namespace ArgusEngine.UnitTests.TechnologyIdentification;

public sealed class CookieExtractorTests
{
    [Fact]
    public void Extract_ParsesFlattenedSetCookieHeadersAndIgnoresCookieAttributes()
    {
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Set-Cookie"] = "session=abc; Path=/; HttpOnly; SameSite=Lax, __cf_bm=token; Max-Age=30; Secure"
        };

        var cookies = CookieExtractor.Extract(
            requestHeaders: new Dictionary<string, string>(),
            responseHeaders: responseHeaders);

        Assert.Equal("abc", cookies["session"]);
        Assert.Equal("token", cookies["__cf_bm"]);
        Assert.DoesNotContain("Path", cookies.Keys);
        Assert.DoesNotContain("HttpOnly", cookies.Keys);
        Assert.DoesNotContain("SameSite", cookies.Keys);
        Assert.DoesNotContain("Max-Age", cookies.Keys);
        Assert.DoesNotContain("Secure", cookies.Keys);
    }

    [Fact]
    public void Extract_RequestCookieHeaderOverridesEarlierResponseCookieValue()
    {
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = "session=new; theme=dark"
        };

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Set-Cookie"] = "session=old; Path=/"
        };

        var cookies = CookieExtractor.Extract(requestHeaders, responseHeaders);

        Assert.Equal("new", cookies["session"]);
        Assert.Equal("dark", cookies["theme"]);
    }

    [Fact]
    public void Extract_ReturnsAnEmptyDictionaryWhenNoCookieHeadersArePresent()
    {
        var cookies = CookieExtractor.Extract(
            requestHeaders: new Dictionary<string, string>(),
            responseHeaders: new Dictionary<string, string>());

        Assert.Empty(cookies);
    }
}
