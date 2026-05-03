namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record RelatedTechnologyRule(
    string TechnologyName,
    int Confidence = 100,
    string? VersionExpression = null);
