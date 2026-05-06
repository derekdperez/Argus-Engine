using AngleSharp.Html.Parser;

namespace ArgusEngine.Workers.TechnologyIdentification;

public sealed class HtmlSignalExtractor
{
    private const int SniffLength = 8 * 1024;

    public static HtmlSignals Extract(string? body, string? contentType, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(body) || !ShouldParse(body, contentType))
            return new HtmlSignals(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

        var parser = new HtmlParser();
        var document = parser.ParseDocument(body);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.QuerySelectorAll("meta"))
        {
            var key = element.GetAttribute("name")
                ?? element.GetAttribute("property")
                ?? element.GetAttribute("http-equiv")
                ?? element.GetAttribute("itemprop");

            var value = element.GetAttribute("content");

            if (!string.IsNullOrWhiteSpace(key) && value is not null)
                meta[key.Trim()] = value;
        }

        var scripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri);

        foreach (var element in document.QuerySelectorAll("script[src]"))
        {
            var src = element.GetAttribute("src");
            if (!string.IsNullOrWhiteSpace(src))
                scripts.Add(ResolveAgainstBaseUrl(src, baseUri));
        }

        return new HtmlSignals(meta, scripts.Count == 0 ? [] : scripts.ToArray());
    }

    private static bool ShouldParse(string body, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = body.AsSpan(0, Math.Min(body.Length, SniffLength));

        return prefix.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<head", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<meta", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAgainstBaseUrl(string src, Uri? baseUri)
    {
        if (Uri.TryCreate(src, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return baseUri is not null
            && Uri.TryCreate(baseUri, src, out var resolved)
            ? resolved.ToString()
            : src;
    }
}
