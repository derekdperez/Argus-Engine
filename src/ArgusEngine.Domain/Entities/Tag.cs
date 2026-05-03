namespace ArgusEngine.Domain.Entities;

public sealed class Tag
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string TagType { get; set; } = "Technology";
    public string Source { get; set; } = "wappalyzer";
    public string? SourceKey { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public string? MetadataJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
