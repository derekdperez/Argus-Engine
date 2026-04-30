using System.Text.RegularExpressions;

namespace NightmareV2.Workers.TechnologyIdentification;

public sealed partial class CookieExtractor
{
    private static readonly HashSet<string> CookieAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "expires",
        "domain",
        "secure",
        "httponly",
        "samesite",
        "max-age",
        "priority",
        "partitioned",
    };

    public IReadOnlyDictionary<string, string> Extract(
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> responseHeaders)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in responseHeaders)
        {
            if (!pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var cookie in SplitFlattenedSetCookie(pair.Value))
                TryAddCookiePair(cookie, cookies);
        }

        foreach (var pair in requestHeaders)
        {
            if (!pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var cookie in pair.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                TryAddCookiePair(cookie, cookies);
        }

        return cookies;
    }

    private static IEnumerable<string> SplitFlattenedSetCookie(string headerValue)
    {
        foreach (var candidate in FlattenedSetCookieSplitter().Split(headerValue))
        {
            var cookie = candidate.Trim();
            if (!string.IsNullOrWhiteSpace(cookie))
                yield return cookie;
        }
    }

    private static void TryAddCookiePair(string candidate, Dictionary<string, string> cookies)
    {
        var firstSegment = candidate.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        var equals = firstSegment.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
            return;

        var name = firstSegment[..equals].Trim();
        var value = firstSegment[(equals + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name) || CookieAttributes.Contains(name))
            return;

        cookies[name] = value;
    }

    [GeneratedRegex(@",\s*(?=[A-Za-z0-9_.$!%*+\-^`|~]+\s*=)", RegexOptions.CultureInvariant)]
    private static partial Regex FlattenedSetCookieSplitter();
}
