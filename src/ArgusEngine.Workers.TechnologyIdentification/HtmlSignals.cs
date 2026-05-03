namespace ArgusEngine.Workers.TechnologyIdentification;

public sealed record HtmlSignals(
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<string> ScriptUrls);
