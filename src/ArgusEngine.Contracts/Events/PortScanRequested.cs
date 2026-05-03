namespace ArgusEngine.Contracts.Events;

/// <summary>
/// Gatekeeper approved an IP (or host resolving to IP) for targeted port discovery.
/// </summary>
public record PortScanRequested(
    Guid TargetId,
    string TargetRootDomain,
    int GlobalMaxDepth,
    int Depth,
    string HostOrIp,
    Guid AssetId,
    Guid CorrelationId,
    Guid EventId = default,
    Guid CausationId = default,
    DateTimeOffset OccurredAtUtc = default,
    string SchemaVersion = "1",
    string Producer = "argus-engine") : IEventEnvelope;
