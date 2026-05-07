using System.Text;
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
        var cappedBody = Cap(input.Body, BodyLimit);
        var cappedSourceUrl = Cap(input.SourceUrl, CandidateLimit) ?? string.Empty;
        var cappedFinalUrl = Cap(input.FinalUrl, CandidateLimit);
        var cappedScriptUrls = CapCandidates(input.ScriptUrls, CandidateLimit);

        foreach (var definition in _catalog.Technologies.Values)
        {
            foreach (var pattern in definition.Patterns)
            {
                TryMatchPattern(
                    pattern,
                    input,
                    cappedBody,
                    cappedSourceUrl,
                    cappedFinalUrl,
                    cappedScriptUrls,
                    results);
            }
        }

        var filtered = ApplyRequires(results);
        filtered = ApplyExcludes(filtered);
        filtered = ApplyImplies(filtered);

        return DeduplicateAndSort(filtered);
    }

    private static void TryMatchPattern(
        TechnologyPattern pattern,
        TechnologyScanInput input,
        string? cappedBody,
        string cappedSourceUrl,
        string? cappedFinalUrl,
        string[] cappedScriptUrls,
        List<TechnologyScanResult> results)
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
                MatchCandidate(pattern, null, cappedBody, results);
                break;

            case TechnologyConstants.ScriptSource:
                for (var i = 0; i < cappedScriptUrls.Length; i++)
                {
                    MatchCandidate(pattern, null, cappedScriptUrls[i], results);
                }

                break;

            case TechnologyConstants.UrlSource:
                MatchCandidate(pattern, "source", cappedSourceUrl, results);

                if (!string.IsNullOrWhiteSpace(cappedFinalUrl)
                    && !string.Equals(cappedSourceUrl, cappedFinalUrl, StringComparison.OrdinalIgnoreCase))
                {
                    MatchCandidate(pattern, "final", cappedFinalUrl, results);
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
        if (values.Count == 0)
            return;

        if (pattern.Key is null)
        {
            foreach (var pair in values)
            {
                MatchCandidate(pattern, pair.Key, Cap(pair.Value, CandidateLimit), results);
            }

            return;
        }

        if (values.TryGetValue(pattern.Key, out var directValue))
        {
            MatchCandidate(pattern, pattern.Key, Cap(directValue, CandidateLimit), results);
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

    private List<TechnologyScanResult> ApplyRequires(List<TechnologyScanResult> results)
    {
        var current = CopyResults(results);
        var changed = true;

        while (changed)
        {
            changed = false;
            var names = BuildTechnologyNameSet(current);
            var keep = new List<TechnologyScanResult>(current.Count);

            for (var i = 0; i < current.Count; i++)
            {
                var result = current[i];
                var definition = _catalog.Find(result.TechnologyName);

                if (definition is null
                    || definition.Requires.Count == 0
                    || ContainsAny(names, definition.Requires))
                {
                    keep.Add(result);
                }
            }

            if (keep.Count != current.Count)
            {
                current = keep;
                changed = true;
            }
        }

        return current;
    }

    private List<TechnologyScanResult> ApplyExcludes(List<TechnologyScanResult> results)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < results.Count; i++)
        {
            var definition = _catalog.Find(results[i].TechnologyName);
            if (definition is null)
                continue;

            foreach (var item in definition.Excludes)
            {
                excluded.Add(item);
            }
        }

        if (excluded.Count == 0)
            return CopyResults(results);

        var output = new List<TechnologyScanResult>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (!excluded.Contains(result.TechnologyName))
            {
                output.Add(result);
            }
        }

        return output;
    }

    private List<TechnologyScanResult> ApplyImplies(List<TechnologyScanResult> results)
    {
        var output = CopyResults(results);
        var present = BuildTechnologyNameSet(output);
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

    private static List<TechnologyScanResult> DeduplicateAndSort(List<TechnologyScanResult> results)
    {
        if (results.Count == 0)
            return [];

        var deduped = new Dictionary<ResultKey, TechnologyScanResult>(
            results.Count,
            ResultKeyComparer.Instance);

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var key = new ResultKey(
                result.TechnologyName,
                result.EvidenceSource,
                result.EvidenceKey,
                result.Pattern,
                result.MatchedText,
                result.Version,
                result.IsImplied);

            if (!deduped.TryGetValue(key, out var existing)
                || result.Confidence > existing.Confidence)
            {
                deduped[key] = result;
            }
        }

        var output = new List<TechnologyScanResult>(deduped.Count);
        foreach (var result in deduped.Values)
        {
            output.Add(result);
        }

        output.Sort(static (left, right) =>
        {
            var confidence = right.Confidence.CompareTo(left.Confidence);
            return confidence != 0
                ? confidence
                : StringComparer.OrdinalIgnoreCase.Compare(left.TechnologyName, right.TechnologyName);
        });

        return output;
    }

    private static string? ResolveVersion(string? expression, Match match)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var replacementCount = CountReplacementTokens(expression);
        if (replacementCount == 0)
            return Cap(expression, 128);

        var builder = new StringBuilder(expression.Length + replacementCount * 8);

        for (var i = 0; i < expression.Length; i++)
        {
            var token = expression[i];
            if ((token != '\\' && token != '$') || i + 1 >= expression.Length || !char.IsDigit(expression[i + 1]))
            {
                builder.Append(token);
                continue;
            }

            var numberStart = i + 1;
            var numberEnd = numberStart;
            var groupIndex = 0;

            while (numberEnd < expression.Length && char.IsDigit(expression[numberEnd]))
            {
                groupIndex = (groupIndex * 10) + (expression[numberEnd] - '0');
                numberEnd++;
            }

            if (groupIndex > 0 && groupIndex < match.Groups.Count)
            {
                builder.Append(match.Groups[groupIndex].Value);
                i = numberEnd - 1;
                continue;
            }

            builder.Append(token);
            builder.Append(expression, numberStart, numberEnd - numberStart);
            i = numberEnd - 1;
        }

        var value = builder.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : Cap(value, 128);
    }

    private static int CountReplacementTokens(string expression)
    {
        var count = 0;

        for (var i = 0; i + 1 < expression.Length; i++)
        {
            if ((expression[i] == '\\' || expression[i] == '$') && char.IsDigit(expression[i + 1]))
            {
                count++;
            }
        }

        return count;
    }

    private static string[] CapCandidates(IReadOnlyList<string> values, int maxLength)
    {
        if (values.Count == 0)
            return [];

        var output = new string[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            output[i] = Cap(values[i], maxLength) ?? string.Empty;
        }

        return output;
    }

    private static List<TechnologyScanResult> CopyResults(List<TechnologyScanResult> results)
    {
        var output = new List<TechnologyScanResult>(results.Count);

        for (var i = 0; i < results.Count; i++)
        {
            output.Add(results[i]);
        }

        return output;
    }

    private static HashSet<string> BuildTechnologyNameSet(List<TechnologyScanResult> results)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < results.Count; i++)
        {
            names.Add(results[i].TechnologyName);
        }

        return names;
    }

    private static bool ContainsAny(HashSet<string> names, IReadOnlyCollection<string> required)
    {
        foreach (var item in required)
        {
            if (names.Contains(item))
                return true;
        }

        return false;
    }

    private static string? Cap(string? value, int maxLength)
    {
        if (value is null)
            return null;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private readonly record struct ResultKey(
        string TechnologyName,
        string EvidenceSource,
        string? EvidenceKey,
        string? Pattern,
        string? MatchedText,
        string? Version,
        bool IsImplied);

    private sealed class ResultKeyComparer : IEqualityComparer<ResultKey>
    {
        internal static readonly ResultKeyComparer Instance = new();

        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        public bool Equals(ResultKey left, ResultKey right) =>
            left.IsImplied == right.IsImplied
            && Comparer.Equals(left.TechnologyName, right.TechnologyName)
            && Comparer.Equals(left.EvidenceSource, right.EvidenceSource)
            && Comparer.Equals(left.EvidenceKey ?? string.Empty, right.EvidenceKey ?? string.Empty)
            && Comparer.Equals(left.Pattern ?? string.Empty, right.Pattern ?? string.Empty)
            && Comparer.Equals(left.MatchedText ?? string.Empty, right.MatchedText ?? string.Empty)
            && Comparer.Equals(left.Version ?? string.Empty, right.Version ?? string.Empty);

        public int GetHashCode(ResultKey value)
        {
            var hash = new HashCode();
            hash.Add(value.TechnologyName, Comparer);
            hash.Add(value.EvidenceSource, Comparer);
            hash.Add(value.EvidenceKey ?? string.Empty, Comparer);
            hash.Add(value.Pattern ?? string.Empty, Comparer);
            hash.Add(value.MatchedText ?? string.Empty, Comparer);
            hash.Add(value.Version ?? string.Empty, Comparer);
            hash.Add(value.IsImplied);
            return hash.ToHashCode();
        }
    }
}
