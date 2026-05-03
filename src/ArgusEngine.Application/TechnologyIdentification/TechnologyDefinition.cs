namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record TechnologyDefinition(
    string Name,
    string? Description,
    string? Website,
    IReadOnlyList<int> CategoryIds,
    IReadOnlyList<TechnologyPattern> Patterns,
    IReadOnlyList<RelatedTechnologyRule> Implies,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Excludes,
    string MetadataJson);
