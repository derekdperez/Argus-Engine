namespace ArgusEngine.Domain.Entities;

public sealed class AssetRelationship
{
    public Guid Id { get; set; }
    public Guid TargetId { get; set; }
    public Guid ParentAssetId { get; set; }
    public StoredAsset? ParentAsset { get; set; }
    public Guid ChildAssetId { get; set; }
    public StoredAsset? ChildAsset { get; set; }
    public string RelationshipType { get; set; } = "";
    public bool IsPrimary { get; set; }
    public decimal Confidence { get; set; } = 1.0m;
    public string DiscoveredBy { get; set; } = "";
    public string DiscoveryContext { get; set; } = "";
    public string? PropertiesJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
