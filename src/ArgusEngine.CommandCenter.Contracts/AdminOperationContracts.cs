namespace ArgusEngine.CommandCenter.Contracts;

public sealed record AdminOperationDto(
    Guid Id,
    string OperationType,
    string Component,
    string? RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? TargetType,
    string? TargetId,
    string? ErrorMessage,
    string? CorrelationId);

public sealed record AdminOperationRequested(
    string OperationType,
    string Component,
    string? RequestedBy,
    string? TargetType,
    string? TargetId,
    string? CorrelationId);
