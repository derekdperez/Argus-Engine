namespace ArgusEngine.CommandCenter.Contracts;

public sealed record EventConsumerTraceDto(
    long JournalId,
    string Worker,
    string ConsumerType,
    string HostName,
    DateTimeOffset ConsumedAtUtc,
    long LatencyMs);

public sealed record EventFollowUpTraceDto(
    long JournalId,
    string EventName,
    string Producer,
    string HostName,
    DateTimeOffset PublishedAtUtc,
    string PayloadPreview);

public sealed record EventTraceRowDto(
    long JournalId,
    string EventName,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset? EventOccurredAtUtc,
    string Producer,
    string HostName,
    Guid? EventId,
    Guid? CorrelationId,
    Guid? CausationId,
    string PayloadPreview,
    string PayloadJson,
    int ConsumerCount,
    IReadOnlyList<EventConsumerTraceDto> Consumers,
    IReadOnlyList<EventFollowUpTraceDto> FollowUps);

public sealed record BusJournalRowDto(
    long Id,
    string Direction,
    string MessageType,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc,
    string? ConsumerType,
    string? HostName);

