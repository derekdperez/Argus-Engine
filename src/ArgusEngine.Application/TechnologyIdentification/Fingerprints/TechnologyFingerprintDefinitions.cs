using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArgusEngine.Application.TechnologyIdentification.Fingerprints;

public sealed record TechnologyFingerprintDefinition(
    string Id,
    FingerprintSource Source,
    TechnologyIdentity Technology,
    string RiskMode,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<FingerprintSignal> Signals,
    IReadOnlyList<FingerprintProbe> Probes,
    string? SupportStage,
    IReadOnlyList<string>? Tags,
    IReadOnlyDictionary<string, JsonElement>? Metadata)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record FingerprintSource(
    string Type,
    string SourceId,
    string? SourcePath,
    string? ContentSha256);

public sealed record TechnologyIdentity(
    string Name,
    string? Vendor,
    string? Product,
    string? Website,
    IReadOnlyList<int>? CategoryIds,
    string? Cpe);

public sealed record FingerprintSignal(
    string? Id,
    string? Mode,
    string? Protocol,
    string? Location,
    string? SourceField,
    string? Key,
    string? Selector,
    string? Attribute,
    FingerprintMatch? Match,
    double? Confidence,
    FingerprintVersionExtraction? Version,
    IReadOnlyList<FingerprintExtractor>? Extractors)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record FingerprintProbe(
    string? Id,
    string? Mode,
    string? Protocol,
    string? SourceField,
    JsonElement? Definition)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record FingerprintMatch(
    string? Type,
    string? Pattern,
    bool? CaseInsensitive,
    string? Value)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record FingerprintExtractor(
    string? Type,
    string? Pattern,
    int? Group)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record FingerprintVersionExtraction(string? Template)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
