namespace ArgusEngine.Contracts.Events;

/// <summary>
/// Idempotent command/event for linking two existing assets within a target graph.
/// </summary>
public record AssetRelationshipDiscovered(
    Guid TargetId,
    Guid ParentAssetId,
    Guid ChildAssetId,
    AssetRelationshipType RelationshipType,
    bool IsPrimary,
    decimal Confidence,
    string DiscoveredBy,
    string DiscoveryContext,
    string PropertiesJson,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    Guid EventId = default,
    Guid CausationId = default,
    string SchemaVersion = "1",
    string Producer = "argus-engine") : IEventEnvelope;
