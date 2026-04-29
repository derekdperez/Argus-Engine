namespace NightmareV2.Application.HighValue;

public sealed record RegexRuleDefinition(
    string Name,
    string Scope,
    string Pattern,
    string Description,
    string OutputFilename,
    int ImportanceScore);
