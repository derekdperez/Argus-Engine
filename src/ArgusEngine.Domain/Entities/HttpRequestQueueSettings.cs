namespace ArgusEngine.Domain.Entities;

public sealed class HttpRequestQueueSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int GlobalRequestsPerMinute { get; set; } = 120_000;
    public int PerDomainRequestsPerMinute { get; set; } = 120;
    public int MaxConcurrency { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
