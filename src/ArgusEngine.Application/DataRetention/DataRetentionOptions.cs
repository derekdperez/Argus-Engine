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
    public int HttpQueueRetentionDays { get; set; } = 30;

    public int CloudUsageRetentionDays { get; set; } = 90;

    public int BatchSize { get; set; } = 1000;
    public int DelayBetweenBatchesMs { get; set; } = 250;
    public int MaxBatchesPerRun { get; set; } = 100;
}
