namespace ArgusEngine.Application.TechnologyIdentification;

public sealed record TechnologyScanResult(
    string TechnologyName,
    string EvidenceSource,
    string? EvidenceKey,
    string? Pattern,
    string? MatchedText,
    string? Version,
    int Confidence,
    bool IsImplied = false);
