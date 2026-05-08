namespace ArgusEngine.CommandCenter.Contracts;

/// <summary>
/// Small, SignalR-friendly notification sent when the command center observes a domain event
/// or an operator changes state through the UI.
/// </summary>
public sealed record LiveUiEventDto(
    string Kind,
    Guid? TargetId,
    Guid? EntityId,
    string Scope,
    string Summary,
    DateTimeOffset OccurredAtUtc);

