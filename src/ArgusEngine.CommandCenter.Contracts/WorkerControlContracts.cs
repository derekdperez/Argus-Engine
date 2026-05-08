namespace ArgusEngine.CommandCenter.Contracts;

public sealed record RestartWorkerRequest(
    string WorkerName,
    string Reason,
    string RequestedBy,
    string? CorrelationId);

public sealed record RestartWorkerResponse(
    string WorkerName,
    string Status,
    string CorrelationId,
    DateTimeOffset AcceptedAtUtc);

public sealed record WorkerScaleRequest(
    string WorkerName,
    int DesiredCount,
    string Reason,
    string RequestedBy,
    string? CorrelationId);

public sealed record WorkerScaleResponse(
    string WorkerName,
    int DesiredCount,
    string Status,
    string CorrelationId,
    DateTimeOffset AcceptedAtUtc);
