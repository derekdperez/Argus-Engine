namespace ArgusEngine.CommandCenter.Models;

public sealed record CommandCenterStatusSnapshot(
    DateTimeOffset AtUtc,
    string Status,
    string Color,
    string Version,
    string BuildStamp,
    IReadOnlyList<CommandCenterComponentStatus> Components,
    IReadOnlyList<CommandCenterWorkerStatus> Workers,
    IReadOnlyList<CommandCenterQueueStatus> Queues,
    IReadOnlyList<CommandCenterDependencyStatus> Dependencies,
    IReadOnlyList<CommandCenterSloIndicator> Indicators,
    IReadOnlyList<CommandCenterAlert> Alerts);

public sealed record CommandCenterComponentStatus(
    string Key,
    string DisplayName,
    string Version,
    string Status,
    string Color,
    string Reason);

public sealed record CommandCenterWorkerStatus(
    string Key,
    string DisplayName,
    int DesiredCount,
    int RunningCount,
    int PendingCount,
    string Status,
    string Color,
    string Reason);

public sealed record CommandCenterQueueStatus(
    string Key,
    string DisplayName,
    long? Depth,
    double? OldestAgeSeconds,
    string Status,
    string Color,
    string Reason);

public sealed record CommandCenterDependencyStatus(
    string Key,
    string DisplayName,
    string Status,
    string Color,
    string Reason);

public sealed record CommandCenterSloIndicator(
    string Name,
    string Target,
    string CurrentValue,
    string Status,
    string Color,
    string Reason);

public sealed record CommandCenterAlert(
    string Severity,
    string Scope,
    string Message,
    DateTimeOffset AtUtc,
    string Color);

