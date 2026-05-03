namespace ArgusEngine.Domain.Entities;

public sealed class AssetTag
{
    public Guid AssetId { get; set; }
    public StoredAsset? Asset { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
