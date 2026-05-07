using ArgusEngine.Contracts.Assets;

namespace ArgusEngine.Contracts.Events;

/// <summary>
/// Published when an HTTP requester successfully (or definitively unsuccessfully) 
/// downloads content for an asset. This triggers the spider to process the content.
/// </summary>
public sealed record HttpResponseDownloaded(
    Guid TargetId,
    string RootDomain,
    int GlobalMaxDepth,
    Guid AssetId,
    int AssetDepth,
    AssetKind AssetKind,
    UrlFetchSnapshot Snapshot,
    DateTimeOffset OccurredAtUtc,
    Guid CorrelationId,
    Guid EventId,
    Guid CausationId,
    string Producer = "worker-http-requester") : IEventEnvelope
{
    public string SchemaVersion => "1.0";
}
