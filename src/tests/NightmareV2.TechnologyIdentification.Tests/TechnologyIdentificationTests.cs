using System.Text.Json;
using System.Text.RegularExpressions;
using NightmareV2.Application.TechnologyIdentification;
using NightmareV2.Workers.TechnologyIdentification;
using Xunit;

namespace NightmareV2.TechnologyIdentification.Tests;

public sealed class TechnologyIdentificationTests
{
    [Fact]
    public void CatalogLoader_LoadsAllTechnologyFiles_AndSkipsInvalidRegex()
    {
        using var fixture = new CatalogFixture();
        fixture.WriteTechnologyFile(
            "a.json",
            new
            {
                Alpha = new
                {
                    headers = new Dictionary<string, string> { ["server"] = "nginx" },
                    js = new { ignored = "window.Alpha" },
                    html = "[",
                    implies = "HTTP/2",
                },
            });
        fixture.WriteTechnologyFile(
            "b.json",
            new
            {
                Beta = new
                {
                    cookies = new Dictionary<string, string> { ["beta"] = "" },
                },
            });

        var catalog = new TechnologyCatalogLoader().Load(fixture.Root);

        Assert.Equal(2, catalog.FilesLoaded);
        Assert.True(catalog.Technologies.ContainsKey("Alpha"));
        Assert.True(catalog.Technologies.ContainsKey("Beta"));
        Assert.Equal(2, catalog.PatternsCompiled);
        Assert.Equal(1, catalog.PatternsSkipped);
        Assert.DoesNotContain(
            catalog.Technologies["Alpha"].Patterns,
            p => p.Source.Equals("js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PatternParser_ParsesConfidenceAndVersionTags()
    {
        var parsed = TechnologyPatternParser.Parse(@"jquery-([0-9.]+)\.js\;version:\1\;confidence:50");

        Assert.Equal(@"jquery-([0-9.]+)\.js", parsed.RegexPattern);
        Assert.Equal(50, parsed.Confidence);
        Assert.Equal(@"\1", parsed.VersionExpression);
    }

    [Fact]
    public void TechnologyTagSlug_NormalizesSpecialNames()
    {
        Assert.Equal("technology:wordpress", TechnologyTagSlug.FromName("WordPress"));
        Assert.Equal("technology:node-js", TechnologyTagSlug.FromName("Node.js"));
        Assert.Equal("technology:csharp", TechnologyTagSlug.FromName("C#"));
        Assert.Equal("technology:cplusplus", TechnologyTagSlug.FromName("C++"));
    }

    [Fact]
    public void Scanner_MatchesResponseHeaderNamesCaseInsensitively()
    {
        var scanner = new TechnologyScanner(CreateCatalog(
            Definition("nginx", Pattern("nginx", TechnologyConstants.HeaderSource, "Server", "nginx"))));
        var input = Input(headers: new Dictionary<string, string> { ["server"] = "nginx/1.25" });

        var results = scanner.Scan(input);

        Assert.Contains(results, r => r.TechnologyName == "nginx" && r.EvidenceSource == TechnologyConstants.HeaderSource);
    }

    [Fact]
    public void CookieExtractor_ParsesFlattenedSetCookie_AndScannerMatchesCookie()
    {
        var cookies = new CookieExtractor().Extract(
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["Set-Cookie"] = "session=abc; Path=/; HttpOnly, wp-settings-time-1=1700000000; expires=Wed, 21 Oct 2030 07:28:00 GMT; Path=/",
            });

        Assert.Equal("abc", cookies["session"]);
        Assert.Equal("1700000000", cookies["wp-settings-time-1"]);

        var scanner = new TechnologyScanner(CreateCatalog(
            Definition("WordPress", Pattern("WordPress", TechnologyConstants.CookieSource, "wp-settings-time-1", ""))));
        var results = scanner.Scan(Input(cookies: cookies));

        Assert.Contains(results, r => r.TechnologyName == "WordPress" && r.EvidenceSource == TechnologyConstants.CookieSource);
    }

    [Fact]
    public void HtmlSignalExtractor_ExtractsMetaAndScriptUrls()
    {
        const string html = """
                            <html>
                              <head>
                                <meta name="generator" content="WordPress 6.5" />
                                <script src="/static/jquery-3.7.1.min.js"></script>
                              </head>
                            </html>
                            """;
        var signals = new HtmlSignalExtractor().Extract(html, "text/html", "https://example.com/index.html");

        Assert.Equal("WordPress 6.5", signals.Meta["generator"]);
        Assert.Contains("https://example.com/static/jquery-3.7.1.min.js", signals.ScriptUrls);

        var scanner = new TechnologyScanner(CreateCatalog(
            Definition("WordPress", Pattern("WordPress", TechnologyConstants.MetaSource, "generator", "WordPress")),
            Definition("jQuery", Pattern("jQuery", TechnologyConstants.ScriptSource, null, @"jquery-([0-9.]+)\.min\.js\;version:\1"))));

        var results = scanner.Scan(Input(meta: signals.Meta, scripts: signals.ScriptUrls));

        Assert.Contains(results, r => r.TechnologyName == "WordPress" && r.EvidenceSource == TechnologyConstants.MetaSource);
        var jquery = Assert.Single(results.Where(r => r.TechnologyName == "jQuery"));
        Assert.Equal("3.7.1", jquery.Version);
    }

    [Fact]
    public void Scanner_MatchesSourceUrlFinalUrlAndBody()
    {
        var scanner = new TechnologyScanner(CreateCatalog(
            Definition("Shopify", Pattern("Shopify", TechnologyConstants.UrlSource, null, "myshopify")),
            Definition("Drupal", Pattern("Drupal", TechnologyConstants.HtmlSource, null, "Drupal.settings"))));

        var results = scanner.Scan(Input(
            sourceUrl: "https://example.com",
            finalUrl: "https://store.myshopify.com",
            body: "<script>Drupal.settings = {}</script>"));

        Assert.Contains(results, r => r.TechnologyName == "Shopify" && r.EvidenceKey == "final");
        Assert.Contains(results, r => r.TechnologyName == "Drupal" && r.EvidenceSource == TechnologyConstants.HtmlSource);
    }

    [Fact]
    public void Scanner_AppliesRequiresExcludesAndImplies()
    {
        var scanner = new TechnologyScanner(CreateCatalog(
            Definition("Framework", Pattern("Framework", TechnologyConstants.HtmlSource, null, "framework"), requires: ["Runtime"]),
            Definition("Runtime", Pattern("Runtime", TechnologyConstants.HtmlSource, null, "runtime"), implies: [new RelatedTechnologyRule("Language")]),
            Definition("Wrong", Pattern("Wrong", TechnologyConstants.HtmlSource, null, "wrong")),
            Definition("Excluder", Pattern("Excluder", TechnologyConstants.HtmlSource, null, "excluder"), excludes: ["Wrong"]),
            Definition("Language")));

        var withoutRequired = scanner.Scan(Input(body: "framework"));
        Assert.DoesNotContain(withoutRequired, r => r.TechnologyName == "Framework");

        var results = scanner.Scan(Input(body: "framework runtime wrong excluder"));

        Assert.Contains(results, r => r.TechnologyName == "Framework");
        Assert.Contains(results, r => r.TechnologyName == "Runtime");
        Assert.Contains(results, r => r.TechnologyName == "Language" && r.IsImplied);
        Assert.Contains(results, r => r.TechnologyName == "Excluder");
        Assert.DoesNotContain(results, r => r.TechnologyName == "Wrong");
    }

    private static TechnologyScanInput Input(
        string sourceUrl = "https://example.com",
        string? finalUrl = null,
        Dictionary<string, string>? headers = null,
        string? body = null,
        IReadOnlyDictionary<string, string>? cookies = null,
        IReadOnlyDictionary<string, string>? meta = null,
        IReadOnlyList<string>? scripts = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            sourceUrl,
            finalUrl,
            headers ?? new Dictionary<string, string>(),
            body,
            "text/html",
            cookies ?? new Dictionary<string, string>(),
            meta ?? new Dictionary<string, string>(),
            scripts ?? []);

    private static TechnologyCatalog CreateCatalog(params TechnologyDefinition[] definitions) =>
        new(
            definitions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, string>(),
            FilesLoaded: 1,
            PatternsCompiled: definitions.Sum(x => x.Patterns.Count),
            PatternsSkipped: 0);

    private static TechnologyDefinition Definition(
        string name,
        TechnologyPattern? pattern = null,
        IReadOnlyList<RelatedTechnologyRule>? implies = null,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<string>? excludes = null) =>
        new(
            name,
            null,
            null,
            [],
            pattern is null ? [] : [pattern],
            implies ?? [],
            requires ?? [],
            excludes ?? [],
            "{}");

    private static TechnologyPattern Pattern(string technology, string source, string? key, string raw)
    {
        var parsed = TechnologyPatternParser.Parse(raw);
        return new TechnologyPattern(
            technology,
            source,
            key,
            raw,
            new Regex(
                parsed.RegexPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(250)),
            parsed.Confidence,
            parsed.VersionExpression);
    }

    private sealed class CatalogFixture : IDisposable
    {
        public CatalogFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "nightmare-tech-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "technologies"));
            File.WriteAllText(Path.Combine(Root, "categories.json"), "{}");
        }

        public string Root { get; }

        public void WriteTechnologyFile(string fileName, object payload)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(Root, "technologies", fileName), json);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
