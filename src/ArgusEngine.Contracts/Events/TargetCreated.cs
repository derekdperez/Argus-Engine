namespace ArgusEngine.Contracts.Events;

/// <summary>
/// User (or automation) registered a new in-scope target root.
/// </summary>
public record TargetCreated(
    Guid TargetId,
    string RootDomain,
    int GlobalMaxDepth,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    Guid EventId = default,
    Guid CausationId = default,
    string SchemaVersion = "1",
    string Producer = "argus-engine") : IEventEnvelope
{
    public DateTimeOffset OccurredAtUtc => OccurredAt;
}
