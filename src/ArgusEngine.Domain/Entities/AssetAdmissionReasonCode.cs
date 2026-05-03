namespace ArgusEngine.Domain.Entities;

public static class AssetAdmissionReasonCode
{
    public const string AcceptedNewAsset = "accepted_new_asset";
    public const string DuplicateCanonicalKey = "duplicate_canonical_key";
    public const string DuplicateWithRelationshipOnly = "duplicate_with_relationship_only";
    public const string GatekeeperDisabled = "gatekeeper_disabled";
    public const string MaxDepthExceeded = "max_depth_exceeded";
    public const string ScopeRejected = "scope_rejected";
    public const string CanonicalizationFailed = "canonicalization_failed";
    public const string PersistenceReturnedEmptyAssetId = "persistence_returned_empty_asset_id";
    public const string ExceptionDuringAdmission = "exception_during_admission";
}
