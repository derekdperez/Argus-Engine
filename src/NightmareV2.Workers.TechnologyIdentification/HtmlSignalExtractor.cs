using AngleSharp.Html.Parser;

namespace NightmareV2.Workers.TechnologyIdentification;

public sealed class HtmlSignalExtractor
{
    private const int SniffLength = 8 * 1024;

    public HtmlSignals Extract(string? body, string? contentType, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(body) || !ShouldParse(body, contentType))
            return new HtmlSignals(new Dictionary<string, string>(), []);

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
                meta[key.Trim().ToLowerInvariant()] = value;
        }

        var scripts = new List<string>();
        foreach (var element in document.QuerySelectorAll("script[src]"))
        {
            var src = element.GetAttribute("src");
            if (!string.IsNullOrWhiteSpace(src))
                scripts.Add(ResolveAgainstBaseUrl(src, sourceUrl));
        }

        return new HtmlSignals(
            meta,
            scripts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool ShouldParse(string body, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = body.Length <= SniffLength ? body : body[..SniffLength];
        return prefix.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<head", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<meta", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAgainstBaseUrl(string src, string sourceUrl)
    {
        if (Uri.TryCreate(src, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, src, out var resolved)
                ? resolved.ToString()
                : src;
    }
}
