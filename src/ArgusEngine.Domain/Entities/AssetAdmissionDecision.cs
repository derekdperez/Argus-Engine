namespace ArgusEngine.Domain.Entities;

public sealed class AssetAdmissionDecision
{
    public Guid Id { get; set; }

    public Guid TargetId { get; set; }

    public Guid? AssetId { get; set; }

    public string RawValue { get; set; } = "";

    public string? CanonicalKey { get; set; }

    public string AssetKind { get; set; } = "";

    public string Decision { get; set; } = "";

    public string ReasonCode { get; set; } = "";

    public string? ReasonDetail { get; set; }

    public string DiscoveredBy { get; set; } = "";

    public string? DiscoveryContext { get; set; }

    public int Depth { get; set; }

    public int GlobalMaxDepth { get; set; }

    public Guid CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public Guid? EventId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
