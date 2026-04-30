namespace NightmareV2.Workers.TechnologyIdentification;

public sealed record HtmlSignals(
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<string> ScriptUrls);
