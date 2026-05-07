namespace ArgusEngine.Application.TechnologyIdentification.Fingerprints;

public sealed record PassiveTechnologyFingerprintInput(
    Guid TargetId,
    Guid AssetId,
    string SourceUrl,
    string? FinalUrl,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? Body,
    string? ContentType,
    IReadOnlyDictionary<string, string> Cookies,
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<string> ScriptUrls);

public sealed record TechnologyObservationDraft(
    Guid TargetId,
    Guid AssetId,
    string FingerprintId,
    string CatalogHash,
    string TechnologyName,
    string? Vendor,
    string? Product,
    string? Version,
    decimal Confidence,
    string SourceType,
    string DetectionMode,
    string DedupeKey,
    IReadOnlyList<TechnologyObservationEvidenceDraft> Evidence);

public sealed record TechnologyObservationEvidenceDraft(
    string SignalId,
    string EvidenceType,
    string? EvidenceKey,
    string? MatchedValueRedacted,
    string EvidenceHash);

public sealed record TechnologyObservationPersistenceResult(
    Guid RunId,
    int ObservationCount,
    int CreatedCount,
    int UpdatedCount,
    int EvidenceAddedCount);

public interface ITechnologyObservationWriter
{
    Task<TechnologyObservationPersistenceResult> UpsertPassiveObservationsAsync(
        Guid targetId,
        IReadOnlyList<TechnologyObservationDraft> observations,
        string catalogHash,
        CancellationToken cancellationToken);
}
