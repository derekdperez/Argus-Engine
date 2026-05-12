namespace ArgusEngine.Application.DataRetention;

public sealed class DataRetentionRunResult
{
    public int SucceededOutboxDeleted { get; set; }
    public int FailedOutboxDeleted { get; set; }
    public int DeadLetterOutboxDeleted { get; set; }
    public int InboxDeleted { get; set; }
    public int BusJournalDeleted { get; set; }

    public int CompletedHttpQueueDeleted { get; set; }
    public int FailedHttpQueueDeleted { get; set; }
    public int StaleQueuedHttpQueueDeleted { get; set; }
    public int StaleRetryHttpQueueDeleted { get; set; }
    public int StaleInFlightHttpQueueDeleted { get; set; }

    // Backwards-compatible total used by existing status/admin UI.
    public int HttpQueueDeleted { get; set; }

    public int CloudUsageDeleted { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
