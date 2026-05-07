using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using ArgusEngine.Application.TechnologyIdentification.Fingerprints;

namespace ArgusEngine.Workers.TechnologyIdentification;

public sealed class PassiveTechnologyFingerprintEngine(ITechnologyFingerprintCatalog catalog)
{
    private const int EvidenceLimit = 512;
    private const int CandidateLimit = 600_000;
    private static readonly ConcurrentDictionary<RegexCacheKey, Regex> RegexCache = new();

    public IReadOnlyList<TechnologyObservationDraft> Evaluate(PassiveTechnologyFingerprintInput input)
    {
        var matches = new List<RawTechnologyEvidenceMatch>();
        IHtmlDocument? document = null;

        foreach (var fingerprint in catalog.Fingerprints)
        {
            if (fingerprint.Signals.Count == 0)
                continue;

            foreach (var signal in fingerprint.Signals)
            {
                if (!IsPassiveHttpSignal(signal))
                    continue;

                document ??= NeedsDocument(signal) ? TryParseDocument(input.Body, input.ContentType) : null;
                EvaluateSignal(input, document, fingerprint, signal, matches);
            }
        }

        return MergeMatches(input.TargetId, input.AssetId, catalog.CatalogHash, matches);
    }

    private static void EvaluateSignal(
        PassiveTechnologyFingerprintInput input,
        IHtmlDocument? document,
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        List<RawTechnologyEvidenceMatch> matches)
    {
        var location = signal.Location ?? "";

        switch (location.ToLowerInvariant())
        {
            case "header":
                EvaluateDictionary(fingerprint, signal, input.ResponseHeaders, location, matches, StringComparer.OrdinalIgnoreCase);
                break;
            case "cookie":
                EvaluateDictionary(fingerprint, signal, input.Cookies, location, matches, StringComparer.Ordinal);
                break;
            case "meta":
                EvaluateDictionary(fingerprint, signal, input.Meta, location, matches, StringComparer.OrdinalIgnoreCase);
                break;
            case "script_src":
                EvaluateCandidates(fingerprint, signal, input.ScriptUrls, location, signal.Key, matches);
                break;
            case "html":
            case "text":
                EvaluateCandidate(fingerprint, signal, input.Body, location, signal.Key, matches);
                break;
            case "script_content":
                if (LooksLikeScriptContent(input.ContentType, input.SourceUrl, input.FinalUrl))
                    EvaluateCandidate(fingerprint, signal, input.Body, location, signal.Key, matches);
                break;
            case "css":
                if (LooksLikeCssContent(input.ContentType, input.SourceUrl, input.FinalUrl))
                    EvaluateCandidate(fingerprint, signal, input.Body, location, signal.Key, matches);
                break;
            case "url":
                EvaluateCandidate(fingerprint, signal, input.SourceUrl, location, "source", matches);
                if (!string.IsNullOrWhiteSpace(input.FinalUrl)
                    && !string.Equals(input.FinalUrl, input.SourceUrl, StringComparison.OrdinalIgnoreCase))
                {
                    EvaluateCandidate(fingerprint, signal, input.FinalUrl, location, "final", matches);
                }

                break;
            case "dom_selector":
                EvaluateDomSelector(fingerprint, signal, document, matches);
                break;
            case "dom_text":
                EvaluateDomText(fingerprint, signal, document, matches);
                break;
            case "dom_attribute":
                EvaluateDomAttribute(fingerprint, signal, document, matches);
                break;
        }
    }

    private static void EvaluateDictionary(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        IReadOnlyDictionary<string, string> values,
        string evidenceType,
        List<RawTechnologyEvidenceMatch> matches,
        StringComparer keyComparer)
    {
        if (values.Count == 0)
            return;

        if (!string.IsNullOrWhiteSpace(signal.Key))
        {
            foreach (var pair in values)
            {
                if (!keyComparer.Equals(pair.Key, signal.Key))
                    continue;

                EvaluateCandidate(fingerprint, signal, pair.Value, evidenceType, pair.Key, matches);
                return;
            }

            if (IsExists(signal))
                return;
        }

        foreach (var pair in values)
        {
            EvaluateCandidate(fingerprint, signal, pair.Value, evidenceType, pair.Key, matches);
        }
    }

    private static void EvaluateCandidates(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        IReadOnlyList<string> candidates,
        string evidenceType,
        string? evidenceKey,
        List<RawTechnologyEvidenceMatch> matches)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            EvaluateCandidate(fingerprint, signal, candidates[i], evidenceType, evidenceKey, matches);
        }
    }

    private static void EvaluateDomSelector(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        IHtmlDocument? document,
        List<RawTechnologyEvidenceMatch> matches)
    {
        if (document is null || string.IsNullOrWhiteSpace(signal.Selector))
            return;

        try
        {
            var element = document.QuerySelector(signal.Selector);
            if (element is null)
                return;

            var candidate = element.OuterHtml;
            EvaluateCandidate(fingerprint, signal, candidate, "dom_selector", signal.Selector, matches);
        }
        catch
        {
            // Invalid selectors in catalog data should skip that signal, not fail the scan.
        }
    }

    private static void EvaluateDomText(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        IHtmlDocument? document,
        List<RawTechnologyEvidenceMatch> matches)
    {
        if (document is null || string.IsNullOrWhiteSpace(signal.Selector))
            return;

        try
        {
            foreach (var element in document.QuerySelectorAll(signal.Selector))
            {
                EvaluateCandidate(fingerprint, signal, element.TextContent, "dom_text", signal.Selector, matches);
            }
        }
        catch
        {
        }
    }

    private static void EvaluateDomAttribute(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        IHtmlDocument? document,
        List<RawTechnologyEvidenceMatch> matches)
    {
        if (document is null || string.IsNullOrWhiteSpace(signal.Selector) || string.IsNullOrWhiteSpace(signal.Attribute))
            return;

        try
        {
            foreach (var element in document.QuerySelectorAll(signal.Selector))
            {
                var value = element.GetAttribute(signal.Attribute);
                EvaluateCandidate(fingerprint, signal, value, "dom_attribute", $"{signal.Selector}@{signal.Attribute}", matches);
            }
        }
        catch
        {
        }
    }

    private static void EvaluateCandidate(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        string? candidate,
        string evidenceType,
        string? evidenceKey,
        List<RawTechnologyEvidenceMatch> matches)
    {
        if (candidate is null)
            return;

        candidate = Cap(candidate, CandidateLimit);
        if (IsExists(signal))
        {
            if (string.IsNullOrEmpty(candidate) && signal.Key is null)
                return;

            AddMatch(fingerprint, signal, evidenceType, evidenceKey, candidate, null, matches);
            return;
        }

        var pattern = signal.Match?.Pattern;
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        try
        {
            var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
            if (signal.Match?.CaseInsensitive is true)
                options |= RegexOptions.IgnoreCase;

            var regex = RegexCache.GetOrAdd(
                new RegexCacheKey(pattern, options),
                static key => new Regex(key.Pattern, key.Options, TimeSpan.FromMilliseconds(250)));
            var match = regex.Match(candidate);
            if (!match.Success)
                return;

            AddMatch(fingerprint, signal, evidenceType, evidenceKey, match.Value, match, matches);
        }
        catch (ArgumentException)
        {
        }
        catch (RegexMatchTimeoutException)
        {
        }
    }

    private static void AddMatch(
        TechnologyFingerprintDefinition fingerprint,
        FingerprintSignal signal,
        string evidenceType,
        string? evidenceKey,
        string matchedValue,
        Match? regexMatch,
        List<RawTechnologyEvidenceMatch> matches)
    {
        var version = ResolveVersion(signal.Version?.Template, regexMatch);
        var confidence = NormalizeConfidence(signal.Confidence, evidenceType, version);
        var signalId = string.IsNullOrWhiteSpace(signal.Id) ? "signal" : signal.Id;
        var redacted = Redact(matchedValue);

        matches.Add(new RawTechnologyEvidenceMatch(
            fingerprint.Id,
            fingerprint.CatalogSafeTechnologyName(),
            fingerprint.Technology.Vendor,
            fingerprint.Technology.Product,
            version,
            fingerprint.Source.Type,
            signalId,
            evidenceType,
            evidenceKey,
            redacted,
            confidence));
    }

    private static TechnologyObservationDraft[] MergeMatches(
        Guid targetId,
        Guid assetId,
        string catalogHash,
        IReadOnlyList<RawTechnologyEvidenceMatch> matches)
    {
        return matches
            .GroupBy(x => new
            {
                x.FingerprintId,
                x.TechnologyName,
                x.Vendor,
                x.Product,
                Version = x.Version ?? "",
                x.SourceType,
            })
            .Select(g =>
            {
                var ordered = g.OrderByDescending(x => x.Confidence).ToArray();
                var combined = CombineConfidence(ordered.Select(x => x.Confidence), ordered.Select(x => x.EvidenceType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                var first = ordered[0];
                var evidence = ordered
                    .Select(x => new TechnologyObservationEvidenceDraft(
                        x.SignalId,
                        x.EvidenceType,
                        x.EvidenceKey,
                        x.MatchedValueRedacted,
                        TechnologyObservationHash.BuildEvidenceHash(
                            x.FingerprintId,
                            x.SignalId,
                            x.EvidenceType,
                            x.EvidenceKey,
                            x.MatchedValueRedacted)))
                    .DistinctBy(x => x.EvidenceHash)
                    .ToArray();

                var dedupeKey = TechnologyObservationHash.BuildDedupeKey(
                    targetId,
                    assetId,
                    first.TechnologyName,
                    first.Vendor,
                    first.Product,
                    first.Version,
                    first.SourceType);

                return new TechnologyObservationDraft(
                    targetId,
                    assetId,
                    first.FingerprintId,
                    catalogHash,
                    first.TechnologyName,
                    first.Vendor,
                    first.Product,
                    first.Version,
                    combined,
                    first.SourceType,
                    "passive",
                    dedupeKey,
                    evidence);
            })
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.TechnologyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPassiveHttpSignal(FingerprintSignal signal) =>
        string.Equals(signal.Mode, "passive", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(signal.Protocol, "http", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(signal.Protocol))
        && signal.Location is not null
        && signal.Location is not ("js_global" or "xhr_url" or "dns_record" or "tls_cert_issuer");

    private static bool NeedsDocument(FingerprintSignal signal) =>
        signal.Location is "dom_selector" or "dom_text" or "dom_attribute";

    private static IHtmlDocument? TryParseDocument(string? body, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return new HtmlParser().ParseDocument(body);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExists(FingerprintSignal signal) =>
        string.Equals(signal.Match?.Type, "exists", StringComparison.OrdinalIgnoreCase);

    private static decimal NormalizeConfidence(double? configuredConfidence, string evidenceType, string? version)
    {
        if (configuredConfidence is > 0)
        {
            var normalized = configuredConfidence.Value > 1 ? configuredConfidence.Value / 100d : configuredConfidence.Value;
            return (decimal)Math.Clamp(normalized, 0.01d, 0.99d);
        }

        return evidenceType switch
        {
            "header" or "cookie" or "meta" => 0.85m,
            "script_src" => 0.70m,
            "script_content" => 0.70m,
            "css" => 0.60m,
            "html" or "text" => 0.65m,
            "dom_selector" => 0.55m,
            "dom_text" or "dom_attribute" => 0.60m,
            "url" => version is null ? 0.60m : 0.85m,
            _ => 0.50m,
        };
    }

    private static decimal CombineConfidence(IEnumerable<decimal> confidences, int independentEvidenceLocations)
    {
        var missProbability = 1m;
        foreach (var confidence in confidences)
        {
            missProbability *= 1m - Math.Clamp(confidence, 0m, 0.99m);
        }

        var combined = 1m - missProbability;
        var cap = independentEvidenceLocations >= 2 ? 0.99m : 0.95m;
        return Math.Min(combined, cap);
    }

    private static string? ResolveVersion(string? template, Match? match)
    {
        if (string.IsNullOrWhiteSpace(template) || match is null)
            return null;

        var value = template;
        for (var i = 1; i < match.Groups.Count; i++)
        {
            value = value.Replace($"\\{i}", match.Groups[i].Value, StringComparison.Ordinal)
                .Replace($"${i}", match.Groups[i].Value, StringComparison.Ordinal);
        }

        value = value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : Cap(value, 128);
    }

    private static string Redact(string value)
    {
        var redacted = value.ReplaceLineEndings(" ").Trim();
        if (redacted.Length > EvidenceLimit)
            redacted = redacted[..EvidenceLimit];

        return redacted;
    }

    private static string Cap(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    private static bool LooksLikeScriptContent(string? contentType, string sourceUrl, string? finalUrl) =>
        ContainsAny(contentType, "javascript", "ecmascript")
        || EndsWithAny(sourceUrl, ".js", ".mjs")
        || EndsWithAny(finalUrl, ".js", ".mjs");

    private static bool LooksLikeCssContent(string? contentType, string sourceUrl, string? finalUrl) =>
        ContainsAny(contentType, "text/css", "css")
        || EndsWithAny(sourceUrl, ".css")
        || EndsWithAny(finalUrl, ".css");

    private static bool ContainsAny(string? value, params string[] needles) =>
        !string.IsNullOrWhiteSpace(value)
        && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool EndsWithAny(string? value, params string[] suffixes)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsolutePath : value;
        return suffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RawTechnologyEvidenceMatch(
        string FingerprintId,
        string TechnologyName,
        string? Vendor,
        string? Product,
        string? Version,
        string SourceType,
        string SignalId,
        string EvidenceType,
        string? EvidenceKey,
        string MatchedValueRedacted,
        decimal Confidence);

    private readonly record struct RegexCacheKey(string Pattern, RegexOptions Options);
}

internal static class TechnologyFingerprintDefinitionExtensions
{
    public static string CatalogSafeTechnologyName(this TechnologyFingerprintDefinition fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint.Technology.Name) ? fingerprint.Id : fingerprint.Technology.Name;
}
