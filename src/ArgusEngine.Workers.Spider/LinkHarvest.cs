using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ArgusEngine.Contracts;

namespace ArgusEngine.Workers.Spider;

internal static class LinkHarvest
{
    private static readonly Regex UrlInText = new(
        @"https?://[^\s""'<>()]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SrcHref = new(
        @"(?:src|href)\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    public static HashSet<string> Extract(string text, string contentType, Uri baseUri) =>
        Extract(text, contentType, baseUri, int.MaxValue);

    public static HashSet<string> Extract(string? text, string? contentType, Uri baseUri, int maxLinks)
    {
        var set = CreateSet(maxLinks);

        if (maxLinks <= 0 || string.IsNullOrEmpty(text))
            return set;

        if (Contains(contentType, "html") || LooksLikeHtml(text))
            return ExtractFromHtml(text, baseUri, maxLinks, set);

        if (Contains(contentType, "markdown") || baseUri.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return ExtractFromMarkdown(text, baseUri, maxLinks, set);

        if (Contains(contentType, "javascript") || baseUri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return ExtractFromScript(text, baseUri, maxLinks, set);

        return ExtractFromPlain(text, baseUri, maxLinks, set);
    }

    private static bool Contains(string? value, string token) =>
        !string.IsNullOrEmpty(value)
        && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHtml(string text) =>
        text.Contains('<', StringComparison.Ordinal)
        && text.Contains('>', StringComparison.Ordinal);

    private static HashSet<string> ExtractFromHtml(string html, Uri baseUri, int maxLinks, HashSet<string> set)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        foreach (var el in doc.QuerySelectorAll("a[href], link[href], area[href], script[src], img[src], iframe[src], embed[src], object[data], source[src], video[src], audio[src], form[action]"))
        {
            var href = el.GetAttribute("href")
                ?? el.GetAttribute("src")
                ?? el.GetAttribute("data")
                ?? el.GetAttribute("action");

            if (!string.IsNullOrWhiteSpace(href)
                && AddIfResolved(set, baseUri, href.AsSpan(), maxLinks))
            {
                return set;
            }
        }

        foreach (var el in doc.QuerySelectorAll("[srcset]"))
        {
            var srcset = el.GetAttribute("srcset");
            if (!string.IsNullOrWhiteSpace(srcset)
                && AddSrcSetLinks(set, baseUri, srcset, maxLinks))
            {
                return set;
            }
        }

        if (doc is IHtmlDocument htmlDoc)
        {
            foreach (var script in htmlDoc.Scripts)
            {
                if (!string.IsNullOrEmpty(script.Source))
                {
                    if (AddIfResolved(set, baseUri, script.Source.AsSpan(), maxLinks))
                        return set;
                }
                else if (!string.IsNullOrEmpty(script.Text)
                    && AddRegexMatches(set, baseUri, script.Text, maxLinks, UrlInText))
                {
                    return set;
                }
            }
        }

        AddRegexMatches(set, baseUri, html, maxLinks, UrlInText);
        return set;
    }

    private static HashSet<string> ExtractFromMarkdown(string markdown, Uri baseUri, int maxLinks, HashSet<string> set)
    {
        var doc = Markdown.Parse(markdown);

        foreach (var node in doc.Descendants())
        {
            string? url = node switch
            {
                LinkInline { Url.Length: > 0 } link => link.Url,
                AutolinkInline { Url.Length: > 0 } autolink => autolink.Url,
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(url)
                && AddIfResolved(set, baseUri, url.AsSpan(), maxLinks))
            {
                return set;
            }
        }

        AddRegexMatches(set, baseUri, markdown, maxLinks, UrlInText);
        return set;
    }

    private static HashSet<string> ExtractFromScript(string script, Uri baseUri, int maxLinks, HashSet<string> set)
    {
        if (AddRegexMatches(set, baseUri, script, maxLinks, UrlInText))
            return set;

        AddRegexMatches(set, baseUri, script, maxLinks, SrcHref, groupIndex: 1);
        return set;
    }

    private static HashSet<string> ExtractFromPlain(string text, Uri baseUri, int maxLinks, HashSet<string> set)
    {
        AddRegexMatches(set, baseUri, text, maxLinks, UrlInText);
        return set;
    }

    private static bool AddRegexMatches(
        HashSet<string> set,
        Uri baseUri,
        string input,
        int maxLinks,
        Regex regex,
        int groupIndex = 0)
    {
        foreach (Match match in regex.Matches(input))
        {
            var value = groupIndex == 0 ? match.Value : match.Groups[groupIndex].Value;
            if (AddIfResolved(set, baseUri, value.AsSpan(), maxLinks))
                return true;
        }

        return false;
    }

    private static bool AddSrcSetLinks(HashSet<string> set, Uri baseUri, string srcset, int maxLinks)
    {
        var remaining = srcset.AsSpan();

        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var candidate = comma >= 0 ? remaining[..comma] : remaining;

            if (comma >= 0)
                remaining = remaining[(comma + 1)..];
            else
                remaining = ReadOnlySpan<char>.Empty;

            candidate = candidate.Trim();
            if (candidate.IsEmpty)
                continue;

            var descriptorStart = IndexOfDescriptorSeparator(candidate);
            if (descriptorStart >= 0)
                candidate = candidate[..descriptorStart];

            if (AddIfResolved(set, baseUri, candidate, maxLinks))
                return true;
        }

        return false;
    }


    private static int IndexOfDescriptorSeparator(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is ' ' or '\t' or '\r' or '\n')
                return i;
        }

        return -1;
    }

    private static bool AddIfResolved(HashSet<string> set, Uri baseUri, ReadOnlySpan<char> raw, int maxLinks)
    {
        if (TryResolve(baseUri, raw, out var absolute))
            set.Add(absolute);

        return set.Count >= maxLinks;
    }

    private static bool TryResolve(Uri baseUri, ReadOnlySpan<char> raw, out string absolute)
    {
        absolute = string.Empty;

        var trimmed = raw.Trim();
        if (trimmed.IsEmpty)
            return false;

        string candidate;

        if (trimmed.Length >= 2 && trimmed[0] == '/' && trimmed[1] == '/')
            candidate = string.Concat(baseUri.Scheme, ":", trimmed.ToString());
        else
            candidate = trimmed.ToString();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            absolute = uri.ToString();
            return true;
        }

        if (Uri.TryCreate(baseUri, candidate, out uri))
        {
            absolute = uri.ToString();
            return true;
        }

        return false;
    }

    private static HashSet<string> CreateSet(int maxLinks)
    {
        var capacity = maxLinks <= 0 || maxLinks == int.MaxValue
            ? 64
            : Math.Min(maxLinks, 256);

        return new HashSet<string>(capacity, StringComparer.OrdinalIgnoreCase);
    }

    public static AssetKind GuessKindForUrl(string url)
    {
        if (url.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return AssetKind.MarkdownBody;

        if (url.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/js/", StringComparison.OrdinalIgnoreCase))
        {
            return AssetKind.JavaScriptFile;
        }

        return AssetKind.Url;
    }
}
