namespace ArgusEngine.Domain.Entities;

public sealed class TechnologyObservation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid TargetId { get; set; }
    public Guid AssetId { get; set; }
    public string FingerprintId { get; set; } = "";
    public string CatalogHash { get; set; } = "";
    public string TechnologyName { get; set; } = "";
    public string? Vendor { get; set; }
    public string? Product { get; set; }
    public string? Version { get; set; }
    public decimal ConfidenceScore { get; set; }
    public string SourceType { get; set; } = "";
    public string DetectionMode { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class TechnologyObservationEvidence
{
    public Guid Id { get; set; }
    public Guid ObservationId { get; set; }
    public string SignalId { get; set; } = "";
    public string EvidenceType { get; set; } = "";
    public string? EvidenceKey { get; set; }
    public string? MatchedValueRedacted { get; set; }
    public Guid? ArtifactId { get; set; }
    public string EvidenceHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
