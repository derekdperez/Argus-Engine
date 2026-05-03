namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record ParsedTechnologyPattern(
    string RegexPattern,
    int Confidence,
    string? VersionExpression);
