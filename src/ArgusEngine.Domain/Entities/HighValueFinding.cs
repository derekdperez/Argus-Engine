namespace ArgusEngine.Domain.Entities;

public sealed class HighValueFinding
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public ReconTarget? Target { get; set; }
    public Guid? SourceAssetId { get; set; }
    public string FindingType { get; set; } = "";
    public string Severity { get; set; } = "";
    public string PatternName { get; set; } = "";
    public string? Category { get; set; }
    public string? MatchedText { get; set; }
    public string SourceUrl { get; set; } = "";
    public string WorkerName { get; set; } = "";
    public int? ImportanceScore { get; set; }
    public DateTimeOffset DiscoveredAtUtc { get; set; }
}
