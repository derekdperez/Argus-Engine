namespace ArgusEngine.CommandCenter.Contracts;

public sealed record ComponentHealthDto(
    string Component,
    string Status,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record CommandCenterStatusDto(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyCollection<ComponentHealthDto> Components);
