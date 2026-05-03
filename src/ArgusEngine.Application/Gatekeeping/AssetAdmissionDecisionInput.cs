namespace ArgusEngine.Application.Gatekeeping;

public sealed record AssetAdmissionDecisionInput(
    Guid TargetId,
    Guid? AssetId,
    string RawValue,
    string? CanonicalKey,
    string AssetKind,
    string Decision,
    string ReasonCode,
    string? ReasonDetail,
    string DiscoveredBy,
    string? DiscoveryContext,
    int Depth,
    int GlobalMaxDepth,
    Guid CorrelationId,
    Guid? CausationId,
    Guid? EventId);
