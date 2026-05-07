namespace ArgusEngine.Application.TechnologyIdentification.Fingerprints;

public sealed record TechnologyFingerprintCatalogValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> UnsupportedCapabilities,
    IReadOnlyList<string> InertFingerprintIds);

public sealed class TechnologyFingerprintCatalogValidationException : InvalidOperationException
{
    public TechnologyFingerprintCatalogValidationException(TechnologyFingerprintCatalogValidationResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    public TechnologyFingerprintCatalogValidationResult Result { get; }

    private static string BuildMessage(TechnologyFingerprintCatalogValidationResult result)
    {
        var sample = string.Join("; ", result.Errors.Take(10));
        return string.IsNullOrWhiteSpace(sample)
            ? "Technology fingerprint catalog is invalid."
            : $"Technology fingerprint catalog is invalid. {sample}";
    }
}

public static class TechnologyFingerprintCatalogValidator
{
    public const string RequiredRiskMode = "technology_identification_only";

    public static readonly ISet<string> KnownCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "http_headers",
        "cookies",
        "html_parser",
        "script_content",
        "dom",
        "browser",
        "xhr",
        "dns",
        "tcp",
        "tls",
        "regex",
        "xpath",
        "dsl",
        "active_http_probe",
        "dns_query",
        "extractor:dsl",
        "extractor:json",
        "extractor:kval",
        "extractor:regex",
        "extractor:xpath",
        "flow_control",
        "headless_browser",
        "matcher:binary",
        "matcher:dsl",
        "matcher:regex",
        "matcher:status",
        "matcher:word",
        "native_protocol_adapter",
        "tcp_probe",
    };

    public static TechnologyFingerprintCatalogValidationResult Validate(
        IReadOnlyList<TechnologyFingerprintDefinition>? fingerprints)
    {
        var errors = new List<string>();
        var unsupportedCapabilities = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (fingerprints is null || fingerprints.Count == 0)
        {
            errors.Add("Catalog must contain a non-empty top-level fingerprint array.");
            return new TechnologyFingerprintCatalogValidationResult(false, errors, [], []);
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inertFingerprintIds = new List<string>();
        for (var i = 0; i < fingerprints.Count; i++)
        {
            var fingerprint = fingerprints[i];
            var label = string.IsNullOrWhiteSpace(fingerprint.Id) ? $"index {i}" : fingerprint.Id;

            if (string.IsNullOrWhiteSpace(fingerprint.Id))
            {
                errors.Add($"Fingerprint at index {i} is missing id.");
            }
            else if (!ids.Add(fingerprint.Id))
            {
                errors.Add($"Duplicate fingerprint id '{fingerprint.Id}'.");
            }

            if (fingerprint.Source is null)
            {
                errors.Add($"Fingerprint '{label}' is missing source.");
            }
            else if (string.IsNullOrWhiteSpace(fingerprint.Source.Type))
            {
                errors.Add($"Fingerprint '{label}' is missing source.type.");
            }

            if (fingerprint.Technology is null)
            {
                errors.Add($"Fingerprint '{label}' is missing technology.");
            }
            else if (string.IsNullOrWhiteSpace(fingerprint.Technology.Name))
            {
                errors.Add($"Fingerprint '{label}' is missing technology.name.");
            }

            if (!string.Equals(fingerprint.RiskMode, RequiredRiskMode, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Fingerprint '{label}' has invalid riskMode '{fingerprint.RiskMode}'.");
            }

            if (fingerprint.Signals.Count == 0 && fingerprint.Probes.Count == 0)
            {
                inertFingerprintIds.Add(label);
            }

            ValidateCapabilities(fingerprint, label, unsupportedCapabilities);
            ValidateSignals(fingerprint.Signals, label, errors);
            ValidateProbes(fingerprint.Probes, label, errors);
        }

        return new TechnologyFingerprintCatalogValidationResult(
            errors.Count == 0,
            errors,
            unsupportedCapabilities.ToArray(),
            inertFingerprintIds);
    }

    public static void ThrowIfInvalid(IReadOnlyList<TechnologyFingerprintDefinition>? fingerprints)
    {
        var result = Validate(fingerprints);
        if (!result.IsValid)
        {
            throw new TechnologyFingerprintCatalogValidationException(result);
        }
    }

    private static void ValidateCapabilities(
        TechnologyFingerprintDefinition fingerprint,
        string label,
        SortedSet<string> unsupportedCapabilities)
    {
        foreach (var capability in fingerprint.RequiredCapabilities)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                unsupportedCapabilities.Add($"{label}:<blank>");
                continue;
            }

            if (!KnownCapabilities.Contains(capability))
            {
                unsupportedCapabilities.Add(capability);
            }
        }
    }

    private static void ValidateSignals(
        IReadOnlyList<FingerprintSignal> signals,
        string fingerprintId,
        List<string> errors)
    {
        for (var i = 0; i < signals.Count; i++)
        {
            var signal = signals[i];
            if (string.IsNullOrWhiteSpace(signal.Id))
            {
                errors.Add($"Fingerprint '{fingerprintId}' signal at index {i} is missing id.");
            }

            if (signal.Match is not null && string.IsNullOrWhiteSpace(signal.Match.Type))
            {
                errors.Add($"Fingerprint '{fingerprintId}' signal '{signal.Id ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)}' has a matcher without type.");
            }
        }
    }

    private static void ValidateProbes(
        IReadOnlyList<FingerprintProbe> probes,
        string fingerprintId,
        List<string> errors)
    {
        for (var i = 0; i < probes.Count; i++)
        {
            var probe = probes[i];
            if (string.IsNullOrWhiteSpace(probe.Id))
            {
                errors.Add($"Fingerprint '{fingerprintId}' probe at index {i} is missing id.");
            }

            if (string.IsNullOrWhiteSpace(probe.Protocol))
            {
                errors.Add($"Fingerprint '{fingerprintId}' probe '{probe.Id ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)}' is missing protocol.");
            }
        }
    }
}
