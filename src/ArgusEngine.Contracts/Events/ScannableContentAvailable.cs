namespace ArgusEngine.Contracts.Events;

/// <summary>
/// Published after HTTP response data for an asset is persisted so workers can scan bodies, headers, and URLs.
/// </summary>
public sealed record ScannableContentAvailable(
    Guid AssetId,
    Guid TargetId,
    string SourceUrl,
    Guid CorrelationId,
    DateTimeOffset StoredAtUtc,
    ScannableContentSource Source,
    Guid EventId = default,
    Guid CausationId = default,
    string SchemaVersion = "1",
    string Producer = "argus-engine") : IEventEnvelope
{
    public DateTimeOffset OccurredAtUtc => StoredAtUtc;
}
