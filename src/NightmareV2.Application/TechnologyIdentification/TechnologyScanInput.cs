namespace NightmareV2.Application.TechnologyIdentification;

public sealed record TechnologyScanInput(
    Guid TargetId,
    Guid AssetId,
    string SourceUrl,
    string? FinalUrl,
    Dictionary<string, string> ResponseHeaders,
    string? Body,
    string? ContentType,
    IReadOnlyDictionary<string, string> Cookies,
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<string> ScriptUrls);
