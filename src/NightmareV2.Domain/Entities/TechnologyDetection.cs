namespace NightmareV2.Domain.Entities;

public sealed class TechnologyDetection
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public Guid AssetId { get; set; }
    public Guid TagId { get; set; }
    public string TechnologyName { get; set; } = "";
    public string EvidenceSource { get; set; } = "";
    public string? EvidenceKey { get; set; }
    public string? Pattern { get; set; }
    public string? MatchedText { get; set; }
    public string? Version { get; set; }
    public decimal Confidence { get; set; } = 1.0m;
    public string EvidenceHash { get; set; } = "";
    public DateTimeOffset DetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
