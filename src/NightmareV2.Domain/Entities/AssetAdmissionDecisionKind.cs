namespace NightmareV2.Domain.Entities;

public static class AssetAdmissionDecisionKind
{
    public const string Accepted = "Accepted";
    public const string Duplicate = "Duplicate";
    public const string OutOfScope = "OutOfScope";
    public const string DepthExceeded = "DepthExceeded";
    public const string Invalid = "Invalid";
    public const string WorkerDisabled = "WorkerDisabled";
    public const string PersistenceSkipped = "PersistenceSkipped";
    public const string Failed = "Failed";
}
