using ArgusEngine.Workers.TechnologyIdentification;
using Xunit;

namespace ArgusEngine.UnitTests.TechnologyIdentification;

public sealed class HtmlSignalExtractorTests
{
    [Fact]
    public void Extract_CollectsMetaSignalsAndResolvesScriptUrlsAgainstSourceUrl()
    {
        const string html = """
            <!doctype html>
            <html>
              <head>
                <meta name="generator" content="WordPress 6.4">
                <meta property="og:site_name" content="Argus">
                <script src="/assets/app.js"></script>
                <script src="https://cdn.example.net/lib.js"></script>
              </head>
            </html>
            """;

        var signals = HtmlSignalExtractor.Extract(
            body: html,
            contentType: "text/html; charset=utf-8",
            sourceUrl: "https://example.com/products/details");

        Assert.Equal("WordPress 6.4", signals.Meta["generator"]);
        Assert.Equal("Argus", signals.Meta["og:site_name"]);
        Assert.Contains("https://example.com/assets/app.js", signals.ScriptUrls);
        Assert.Contains("https://cdn.example.net/lib.js", signals.ScriptUrls);
    }

    [Fact]
    public void Extract_DeduplicatesScriptUrlsCaseInsensitively()
    {
        const string html = """
            <html>
              <head>
                <script src="https://cdn.example.net/app.js"></script>
                <script src="https://CDN.example.net/app.js"></script>
              </head>
            </html>
            """;

        var signals = HtmlSignalExtractor.Extract(html, "text/html", "https://example.com/");

        Assert.Single(signals.ScriptUrls);
    }

    [Fact]
    public void Extract_DoesNotTreatRootRelativeScriptPathAsFileUriWhenNoBaseUrlIsAvailable()
    {
        const string html = """
            <html>
              <head>
                <script src="/assets/app.js"></script>
              </head>
            </html>
            """;

        var signals = HtmlSignalExtractor.Extract(html, "text/html", "not-a-valid-url");

        Assert.Single(signals.ScriptUrls);
        Assert.Contains("/assets/app.js", signals.ScriptUrls);
    }

    [Theory]
    [InlineData(null, "text/html")]
    [InlineData("", "text/html")]
    [InlineData("{\"ok\":true}", "application/json")]
    public void Extract_IgnoresBlankOrNonHtmlBodies(string? body, string? contentType)
    {
        var signals = HtmlSignalExtractor.Extract(body, contentType, "https://example.com/");

        Assert.Empty(signals.Meta);
        Assert.Empty(signals.ScriptUrls);
    }
}
