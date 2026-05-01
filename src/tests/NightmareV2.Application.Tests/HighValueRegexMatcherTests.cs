using NightmareV2.Application.Assets;
using NightmareV2.Application.HighValue;
using Xunit;

namespace NightmareV2.Application.Tests;

public sealed class HighValueRegexMatcherTests
{
    [Fact]
    public void ScanUrlHttpExchange_MatchesConfiguredScopesAndBuildsRequestResponseHaystack()
    {
        var matcher = new HighValueRegexMatcher(
        [
            new RegexRuleDefinition("aws_key", "file_contents", "AKIA[0-9A-Z]{16}", "AWS key", "aws.txt", 10),
            new RegexRuleDefinition("admin_url", "url", "/admin", "Admin URL", "admin.txt", 8),
            new RegexRuleDefinition("server_header", "request_response", "Server: nginx", "Server disclosure", "server.txt", 5),
        ]);

        var snapshot = new UrlFetchSnapshot(
            "GET",
            new Dictionary<string, string> { ["Accept"] = "text/html" },
            null,
            200,
            new Dictionary<string, string> { ["Server"] = "nginx" },
            "token=AKIA1234567890ABCDEF",
            27,
            10,
            "text/plain",
            DateTimeOffset.UtcNow);

        var hits = matcher.ScanUrlHttpExchange("https://example.com/admin", snapshot).ToArray();

        Assert.Contains(hits, h => h.PatternName == "aws_key" && h.Scope == "file_contents" && h.ImportanceScore == 10);
        Assert.Contains(hits, h => h.PatternName == "admin_url" && h.Scope == "url");
        Assert.Contains(hits, h => h.PatternName == "server_header" && h.Scope == "request_response");
    }

    [Fact]
    public void ScanUrlHttpExchange_SkipsInvalidRulesAndUnknownScopes()
    {
        var matcher = new HighValueRegexMatcher(
        [
            new RegexRuleDefinition("bad", "file_contents", "[", "Invalid regex", "bad.txt", 10),
            new RegexRuleDefinition("unknown", "headers_only", "secret", "Unknown scope", "unknown.txt", 10),
        ]);

        var snapshot = new UrlFetchSnapshot(
            "GET",
            new Dictionary<string, string>(),
            null,
            200,
            new Dictionary<string, string>(),
            "secret",
            6,
            1,
            "text/plain",
            DateTimeOffset.UtcNow);

        Assert.Empty(matcher.ScanUrlHttpExchange("https://example.com", snapshot));
    }

    [Fact]
    public void ScanUrlHttpExchange_TruncatesLongSnippets()
    {
        var matcher = new HighValueRegexMatcher(
        [
            new RegexRuleDefinition("long", "file_contents", "a{500}", "Long match", "long.txt", 6),
        ]);
        var snapshot = new UrlFetchSnapshot(
            "GET",
            new Dictionary<string, string>(),
            null,
            200,
            new Dictionary<string, string>(),
            new string('a', 500),
            500,
            1,
            "text/plain",
            DateTimeOffset.UtcNow);

        var hit = Assert.Single(matcher.ScanUrlHttpExchange("https://example.com", snapshot));

        Assert.Equal(401, hit.MatchedSnippet.Length);
        Assert.EndsWith("…", hit.MatchedSnippet);
    }
}
