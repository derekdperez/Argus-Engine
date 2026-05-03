namespace ArgusEngine.Application.HighValue;

public sealed record RegexMatchHit(
    string PatternName,
    string Scope,
    string MatchedSnippet,
    int ImportanceScore);
