using NightmareV2.Contracts;
using NightmareV2.Workers.Spider;
using Xunit;

namespace NightmareV2.Workers.Spider.Tests;

public sealed class LinkHarvestTests
{
    [Fact]
    public void Extract_Html_ResolvesLinksAssetsSrcsetFormsAndInlineScriptUrls()
    {
        const string html = """
                            <html>
                              <head>
                                <link href="/site.css">
                                <script src="/app.js"></script>
                                <script>const api = "https://api.example.com/v1/users";</script>
                              </head>
                              <body>
                                <a href="/login">Login</a>
                                <img srcset="/small.png 1x, //cdn.example.com/large.png 2x">
                                <form action="../submit"></form>
                              </body>
                            </html>
                            """;

        var links = LinkHarvest.Extract(html, "text/html", new Uri("https://example.com/docs/index.html")).ToArray();

        Assert.Contains("https://example.com/login", links);
        Assert.Contains("https://example.com/site.css", links);
        Assert.Contains("https://example.com/app.js", links);
        Assert.Contains("https://api.example.com/v1/users", links);
        Assert.Contains("https://example.com/small.png", links);
        Assert.Contains("https://cdn.example.com/large.png", links);
        Assert.Contains("https://example.com/submit", links);
        Assert.Equal(links.Length, links.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Extract_Markdown_ResolvesMarkdownAndAutolinks()
    {
        const string markdown = """
                                [relative](../docs/api.md)
                                <https://example.com/absolute>
                                """;

        var links = LinkHarvest.Extract(markdown, "text/markdown", new Uri("https://example.com/app/readme.md")).ToArray();

        Assert.Contains("https://example.com/docs/api.md", links);
        Assert.Contains("https://example.com/absolute", links);
    }

    [Fact]
    public void Extract_JavaScript_FindsRawUrlsAndSrcHrefAssignments()
    {
        const string script = """
                              const url = "https://api.example.com/v1/status";
                              const template = `<img src='/static/logo.png'>`;
                              """;

        var links = LinkHarvest.Extract(script, "application/javascript", new Uri("https://example.com/assets/app.js")).ToArray();

        Assert.Contains("https://api.example.com/v1/status", links);
        Assert.Contains("https://example.com/static/logo.png", links);
    }

    [Fact]
    public void Extract_PlainText_FindsOnlyAbsoluteHttpUrls()
    {
        var links = LinkHarvest.Extract(
            "See https://example.com/a and ftp://example.com/nope and /relative",
            "text/plain",
            new Uri("https://example.com/base"))
            .ToArray();

        Assert.Equal(["https://example.com/a"], links);
    }

    [Theory]
    [InlineData("https://example.com/readme.md", AssetKind.MarkdownBody)]
    [InlineData("https://example.com/assets/app.js", AssetKind.JavaScriptFile)]
    [InlineData("https://example.com/js/app", AssetKind.JavaScriptFile)]
    [InlineData("https://example.com/", AssetKind.Url)]
    public void GuessKindForUrl_MapsKnownFetchableFileTypes(string url, AssetKind expected)
    {
        Assert.Equal(expected, LinkHarvest.GuessKindForUrl(url));
    }
}
