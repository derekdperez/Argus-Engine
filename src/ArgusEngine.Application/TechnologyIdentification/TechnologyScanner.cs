using System.Text.RegularExpressions;

namespace ArgusEngine.Application.TechnologyIdentification;

public sealed class TechnologyScanner(TechnologyCatalog catalog)
{
    private const int BodyLimit = 600_000;
    private const int CandidateLimit = 16_384;
    private const int EvidenceLimit = 300;

    private readonly TechnologyCatalog _catalog = catalog;

    public IReadOnlyList<TechnologyScanResult> Scan(TechnologyScanInput input)
    {
        var results = new List<TechnologyScanResult>();
        foreach (var definition in _catalog.Technologies.Values)
        {
            foreach (var pattern in definition.Patterns)
                TryMatchPattern(pattern, input, results);
        }

        var filtered = ApplyRequires(results);
        filtered = ApplyExcludes(filtered);
        filtered = ApplyImplies(filtered);

        return filtered
            .GroupBy(
                r => string.Join(
                    "\u001f",
                    r.TechnologyName,
                    r.EvidenceSource,
                    r.EvidenceKey ?? "",
                    r.Pattern ?? "",
                    r.MatchedText ?? "",
                    r.Version ?? "",
                    r.IsImplied).ToLowerInvariant(),
                StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.TechnologyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void TryMatchPattern(TechnologyPattern pattern, TechnologyScanInput input, List<TechnologyScanResult> results)
    {
        switch (pattern.Source)
        {
            case TechnologyConstants.HeaderSource:
                MatchKeyedDictionary(pattern, input.ResponseHeaders, results, StringComparer.OrdinalIgnoreCase);
                break;
            case TechnologyConstants.CookieSource:
                MatchKeyedDictionary(pattern, input.Cookies, results, StringComparer.Ordinal);
                break;
            case TechnologyConstants.MetaSource:
                MatchKeyedDictionary(pattern, input.Meta, results, StringComparer.OrdinalIgnoreCase);
                break;
            case TechnologyConstants.HtmlSource:
                MatchCandidate(pattern, null, Cap(input.Body, BodyLimit), results);
                break;
            case TechnologyConstants.ScriptSource:
                foreach (var script in input.ScriptUrls)
                    MatchCandidate(pattern, null, Cap(script, CandidateLimit), results);
                break;
            case TechnologyConstants.UrlSource:
                MatchCandidate(pattern, "source", Cap(input.SourceUrl, CandidateLimit), results);
                if (!string.IsNullOrWhiteSpace(input.FinalUrl)
                    && !string.Equals(input.SourceUrl, input.FinalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    MatchCandidate(pattern, "final", Cap(input.FinalUrl, CandidateLimit), results);
                }
                break;
        }
    }

    private static void MatchKeyedDictionary(
        TechnologyPattern pattern,
        IReadOnlyDictionary<string, string> values,
        List<TechnologyScanResult> results,
        StringComparer keyComparer)
    {
        if (pattern.Key is null)
        {
            foreach (var pair in values)
                MatchCandidate(pattern, pair.Key, Cap(pair.Value, CandidateLimit), results);
            return;
        }

        foreach (var pair in values)
        {
            if (!keyComparer.Equals(pair.Key, pattern.Key))
                continue;

            MatchCandidate(pattern, pair.Key, Cap(pair.Value, CandidateLimit), results);
            return;
        }
    }

    private static void MatchCandidate(
        TechnologyPattern pattern,
        string? evidenceKey,
        string? candidate,
        List<TechnologyScanResult> results)
    {
        if (candidate is null)
            return;

        try
        {
            var match = pattern.Regex.Match(candidate);
            if (!match.Success)
                return;

            results.Add(
                new TechnologyScanResult(
                    pattern.TechnologyName,
                    pattern.Source,
                    evidenceKey ?? pattern.Key,
                    pattern.RawPattern,
                    Cap(match.Value, EvidenceLimit),
                    ResolveVersion(pattern.VersionExpression, match),
                    pattern.Confidence));
        }
        catch (RegexMatchTimeoutException)
        {
            // A single hostile candidate must not fail the entire asset scan.
        }
    }

    private List<TechnologyScanResult> ApplyRequires(IReadOnlyList<TechnologyScanResult> results)
    {
        var current = results.ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            var names = current.Select(x => x.TechnologyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keep = current
                .Where(result =>
                {
                    var definition = _catalog.Find(result.TechnologyName);
                    return definition is null
                        || definition.Requires.Count == 0
                        || definition.Requires.Any(names.Contains);
                })
                .ToList();

            if (keep.Count != current.Count)
            {
                current = keep;
                changed = true;
            }
        }

        return current;
    }

    private List<TechnologyScanResult> ApplyExcludes(IReadOnlyList<TechnologyScanResult> results)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var definition = _catalog.Find(result.TechnologyName);
            if (definition is null)
                continue;

            foreach (var item in definition.Excludes)
                excluded.Add(item);
        }

        if (excluded.Count == 0)
            return results.ToList();

        return results
            .Where(result => !excluded.Contains(result.TechnologyName))
            .ToList();
    }

    private List<TechnologyScanResult> ApplyImplies(IReadOnlyList<TechnologyScanResult> results)
    {
        var output = results.ToList();
        var present = output.Select(x => x.TechnologyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(present);

        while (queue.Count > 0)
        {
            var technology = queue.Dequeue();
            var definition = _catalog.Find(technology);
            if (definition is null)
                continue;

            foreach (var implied in definition.Implies)
            {
                if (!present.Add(implied.TechnologyName))
                    continue;

                output.Add(
                    new TechnologyScanResult(
                        implied.TechnologyName,
                        TechnologyConstants.ImpliedSource,
                        technology,
                        null,
                        technology,
                        null,
                        implied.Confidence,
                        IsImplied: true));
                queue.Enqueue(implied.TechnologyName);
            }
        }

        return output;
    }

    private static string? ResolveVersion(string? expression, Match match)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var value = expression;
        for (var i = 1; i < match.Groups.Count; i++)
            value = value.Replace($"\\{i}", match.Groups[i].Value, StringComparison.Ordinal)
                .Replace($"${i}", match.Groups[i].Value, StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(value) ? null : Cap(value, 128);
    }

    private static string? Cap(string? value, int maxLength)
    {
        if (value is null)
            return null;

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
