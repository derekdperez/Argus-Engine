namespace ArgusEngine.Domain.Entities;

public sealed class CloudResourceUsageSample
{
    public long Id { get; set; }
    public DateTimeOffset SampledAtUtc { get; set; }
    public string ResourceKind { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public int RunningCount { get; set; }
    public string? MetadataJson { get; set; }
}
