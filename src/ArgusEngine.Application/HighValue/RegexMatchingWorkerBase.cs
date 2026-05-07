using System.Text;
using System.Text.RegularExpressions;
using ArgusEngine.Application.Assets;
using ArgusEngine.Contracts.Assets;

namespace ArgusEngine.Application.HighValue;

/// <summary>
/// Generic regex runner over URL / file body / full request+response text. Subclass or compose for specialized workers.
/// </summary>
public abstract class RegexMatchingWorkerBase
{
    private const int MaxSnippetChars = 400;
    private const int MaxHaystackChars = 600_000;

    protected abstract IReadOnlyList<CompiledRegexRule> Rules { get; }

    public IEnumerable<RegexMatchHit> ScanUrlHttpExchange(string sourceUrl, UrlFetchSnapshot snapshot)
    {
        var fileHaystack = Truncate(snapshot.ResponseBody ?? "", MaxHaystackChars);
        var requestResponseHaystack = Truncate(BuildRequestResponseHaystack(snapshot), MaxHaystackChars);
        var urlHaystack = sourceUrl ?? "";

        foreach (var rule in Rules)
        {
            string? haystack = rule.Definition.Scope switch
            {
                "file_contents" => fileHaystack,
                "request_response" => requestResponseHaystack,
                "url" => urlHaystack,
                _ => null,
            };
            if (haystack is null)
                continue;

            MatchCollection? matches = null;
            try
            {
                matches = rule.Regex.Matches(haystack);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            foreach (Match m in matches)
            {
                if (!m.Success || m.Length == 0)
                    continue;
                var snippet = Truncate(m.Value, MaxSnippetChars);
                yield return new RegexMatchHit(rule.Definition.Name, rule.Definition.Scope, snippet, rule.Definition.ImportanceScore);
            }
        }
    }

    private static string BuildRequestResponseHaystack(UrlFetchSnapshot s)
    {
        var sb = new StringBuilder(8192);
        sb.Append(s.RequestMethod).Append(' ').Append(s.StatusCode).Append('\n');
        AppendHeaders(sb, s.RequestHeaders);
        sb.Append('\n');
        AppendHeaders(sb, s.ResponseHeaders);
        sb.Append('\n');
        sb.Append(s.ResponseBody ?? "");
        return sb.ToString();
    }

    private static void AppendHeaders(StringBuilder sb, Dictionary<string, string> headers)
    {
        foreach (var kv in headers.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    protected static IReadOnlyList<CompiledRegexRule> CompileDefinitions(IReadOnlyList<RegexRuleDefinition> defs)
    {
        var list = new List<CompiledRegexRule>();
        foreach (var d in defs)
        {
            try
            {
                var r = new Regex(
                    d.Pattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(250));
                list.Add(new CompiledRegexRule(d, r));
            }
            catch
            {
                // skip invalid patterns
            }
        }

        return list;
    }
}

public sealed record CompiledRegexRule(RegexRuleDefinition Definition, Regex Regex);
