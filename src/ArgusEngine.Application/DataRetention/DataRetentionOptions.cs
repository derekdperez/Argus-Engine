namespace ArgusEngine.Application.DataRetention;

public sealed class DataRetentionOptions
{
    public bool Enabled { get; set; } = true;

    // Event telemetry is intentionally short-lived. Bus journal/outbox rows are
    // high-volume operational telemetry, not asset evidence. Keeping them too
    // long causes the Operations storage card to be dominated by event data.
    public int SucceededOutboxRetentionDays { get; set; } = 1;
    public int FailedOutboxRetentionDays { get; set; } = 7;
    public int DeadLetterOutboxRetentionDays { get; set; } = 30;
    public int InboxRetentionDays { get; set; } = 7;
    public int BusJournalRetentionDays { get; set; } = 2;

    // By default, expired event rows are disposed instead of archived. Archiving
    // historical telemetry preserves volume elsewhere and does not solve local
    // storage pressure. Set this true only when you need a short audit archive.
    public bool ArchiveEventTablesBeforeDelete { get; set; } = false;

    // If event archiving is explicitly enabled, keep that archive short-lived.
    public int ArchivedEventRetentionDays { get; set; } = 1;

    public int CompletedHttpQueueRetentionDays { get; set; } = 7;
    public int FailedHttpQueueRetentionDays { get; set; } = 14;

    // Kept for backwards-compatible configuration. If Completed/Failed-specific
    // retention values are not configured, callers may still use this value.
    public int HttpQueueRetentionDays { get; set; } = 7;

    // Purges abandoned queued/retry work that is too old to be useful. This is
    // intentionally configurable because local/dev deployments can generate very
    // large HTTP queues while tuning workers and autoscaling.
    public bool PurgeStaleHttpQueueItems { get; set; } = true;
    public int StaleQueuedHttpQueueRetentionHours { get; set; } = 24;
    public int StaleRetryHttpQueueRetentionHours { get; set; } = 24;
    public int StaleInFlightHttpQueueRetentionHours { get; set; } = 6;

    public int CloudUsageRetentionDays { get; set; } = 90;

    // Larger batches reduce high-volume event tables quickly while remaining
    // inside the existing AddArgusInfrastructure validation bounds.
    public int BatchSize { get; set; } = 5000;
    public int DelayBetweenBatchesMs { get; set; } = 50;
    public int MaxBatchesPerRun { get; set; } = 500;
}
