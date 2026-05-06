namespace ArgusEngine.Workers.TechnologyIdentification;

public sealed class CookieExtractor
{
    private static readonly string[] CookieAttributes =
    [
        "path",
        "expires",
        "domain",
        "secure",
        "httponly",
        "samesite",
        "max-age",
        "priority",
        "partitioned",
    ];

    public static IReadOnlyDictionary<string, string> Extract(
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, string> responseHeaders)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in responseHeaders)
        {
            if (pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                AddFlattenedSetCookiePairs(pair.Value.AsSpan(), cookies);
        }

        foreach (var pair in requestHeaders)
        {
            if (pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                AddRequestCookiePairs(pair.Value.AsSpan(), cookies);
        }

        return cookies;
    }

    private static void AddFlattenedSetCookiePairs(
        ReadOnlySpan<char> headerValue,
        Dictionary<string, string> cookies)
    {
        var segmentStart = 0;

        for (var i = 0; i <= headerValue.Length; i++)
        {
            if (i < headerValue.Length && !IsCookieBoundary(headerValue, i))
                continue;

            TryAddCookiePair(headerValue[segmentStart..i], cookies);
            segmentStart = i + 1;
        }
    }

    private static void AddRequestCookiePairs(
        ReadOnlySpan<char> headerValue,
        Dictionary<string, string> cookies)
    {
        var segmentStart = 0;

        for (var i = 0; i <= headerValue.Length; i++)
        {
            if (i < headerValue.Length && headerValue[i] != ';')
                continue;

            TryAddCookiePair(headerValue[segmentStart..i], cookies);
            segmentStart = i + 1;
        }
    }

    private static bool IsCookieBoundary(ReadOnlySpan<char> headerValue, int index)
    {
        if (headerValue[index] != ',')
            return false;

        var lookahead = headerValue[(index + 1)..].TrimStart();

        if (lookahead.Length == 0 || !IsCookieNameChar(lookahead[0]))
            return false;

        for (var i = 1; i < lookahead.Length; i++)
        {
            var current = lookahead[i];

            if (current == '=')
                return true;

            if (char.IsWhiteSpace(current))
                continue;

            if (!IsCookieNameChar(current))
                return false;
        }

        return false;
    }

    private static void TryAddCookiePair(
        ReadOnlySpan<char> candidate,
        Dictionary<string, string> cookies)
    {
        var firstSegment = candidate;
        var semicolon = firstSegment.IndexOf(';');

        if (semicolon >= 0)
            firstSegment = firstSegment[..semicolon];

        firstSegment = firstSegment.Trim();

        if (firstSegment.Length == 0)
            return;

        var equals = firstSegment.IndexOf('=');

        if (equals <= 0)
            return;

        var name = firstSegment[..equals].Trim();
        var value = firstSegment[(equals + 1)..].Trim();

        if (name.Length == 0 || IsCookieAttribute(name))
            return;

        cookies[name.ToString()] = value.ToString();
    }

    private static bool IsCookieAttribute(ReadOnlySpan<char> name)
    {
        foreach (var attribute in CookieAttributes)
        {
            if (name.Equals(attribute, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsCookieNameChar(char value) =>
        char.IsAsciiLetterOrDigit(value)
        || value is '_' or '.' or '$' or '!' or '%' or '*' or '+' or '-' or '^' or '`' or '|' or '~';
}
