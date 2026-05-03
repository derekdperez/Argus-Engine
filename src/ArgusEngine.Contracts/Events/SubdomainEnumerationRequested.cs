namespace ArgusEngine.Contracts.Events;

/// <summary>
/// Requests one provider-specific subdomain enumeration job for a target root.
/// </summary>
public record SubdomainEnumerationRequested(
    Guid TargetId,
    string RootDomain,
    string Provider,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    Guid CorrelationId,
    Guid EventId = default,
    Guid CausationId = default,
    string SchemaVersion = "1",
    string Producer = "argus-engine") : IEventEnvelope
{
    public DateTimeOffset OccurredAtUtc => RequestedAt;
}
