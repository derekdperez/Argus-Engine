namespace ArgusEngine.Application.Orchestration;

public interface IReconProfileAssignmentService
{
    Task<ReconHeaderProfile?> GetOrCreateProfileAsync(
        ReconProfileAssignmentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ReconProfileAssignmentRequest(
    Guid TargetId,
    string SubdomainKey,
    string MachineKey,
    string? MachineName,
    string? PublicIpAddress,
    DateTimeOffset RequestedAtUtc);

public sealed record ReconHeaderProfile(
    Guid AssignmentId,
    Guid TargetId,
    string SubdomainKey,
    string MachineKey,
    int ProfileIndex,
    string DeviceType,
    string Browser,
    string OperatingSystem,
    int HardwareAgeYears,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    bool RandomDelayEnabled,
    int RandomDelayMinMs,
    int RandomDelayMaxMs,
    int RequestsPerMinutePerSubdomain,
    int HeaderOrderSeed);
