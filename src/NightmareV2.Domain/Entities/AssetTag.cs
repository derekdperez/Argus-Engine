namespace NightmareV2.Domain.Entities;

public sealed class AssetTag
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public Guid AssetId { get; set; }
    public StoredAsset? Asset { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
    public decimal Confidence { get; set; } = 1.0m;
    public string Source { get; set; } = "technology-identification";
    public string? EvidenceJson { get; set; }
    public DateTimeOffset FirstSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
