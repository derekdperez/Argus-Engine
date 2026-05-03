namespace ArgusEngine.Domain.Entities;

public sealed class CloudResourceUsageSample
{
    public Guid Id { get; set; }
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public decimal UsageValue { get; set; }
    public string Unit { get; set; } = "";
    public DateTimeOffset SampledAtUtc { get; set; }
}
