namespace ArgusEngine.Application.DataRetention;

public sealed class DataRetentionOptions
{
    public bool Enabled { get; set; } = true;

    public int SucceededOutboxRetentionDays { get; set; } = 7;
    public int FailedOutboxRetentionDays { get; set; } = 30;
    public int DeadLetterOutboxRetentionDays { get; set; } = 90;
    public int InboxRetentionDays { get; set; } = 30;
    public int BusJournalRetentionDays { get; set; } = 14;

    public int CompletedHttpQueueRetentionDays { get; set; } = 30;
    public int FailedHttpQueueRetentionDays { get; set; } = 90;

    // Kept for backwards-compatible configuration. If Completed/Failed-specific
    // retention values are not configured, callers may still use this value.
    public int HttpQueueRetentionDays { get; set; } = 30;

    // Purges abandoned queued/retry work that is too old to be useful. This is
    // intentionally configurable because local/dev deployments can generate very
    // large HTTP queues while tuning workers and autoscaling.
    public bool PurgeStaleHttpQueueItems { get; set; } = true;
    public int StaleQueuedHttpQueueRetentionHours { get; set; } = 24;
    public int StaleRetryHttpQueueRetentionHours { get; set; } = 24;
    public int StaleInFlightHttpQueueRetentionHours { get; set; } = 6;

    public int CloudUsageRetentionDays { get; set; } = 90;

    public int BatchSize { get; set; } = 1000;
    public int DelayBetweenBatchesMs { get; set; } = 250;
    public int MaxBatchesPerRun { get; set; } = 100;
}
