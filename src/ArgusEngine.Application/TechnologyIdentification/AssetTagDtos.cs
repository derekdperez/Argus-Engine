namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record AssetTagDto(
    Guid TagId,
    string Slug,
    string Name,
    string TagType,
    decimal Confidence,
    string? EvidenceJson,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc);

public sealed record TargetTechnologyDto(
    Guid TagId,
    string Slug,
    string Name,
    string TagType,
    long AssetCount,
    decimal MaxConfidence,
    DateTimeOffset LastSeenAtUtc);
