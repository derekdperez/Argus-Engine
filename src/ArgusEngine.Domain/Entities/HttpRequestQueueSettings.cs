namespace ArgusEngine.Domain.Entities;

public sealed class HttpRequestQueueSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int GlobalRequestsPerMinute { get; set; } = 120_000;
    public int PerDomainRequestsPerMinute { get; set; } = 120;
    public int MaxConcurrency { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 30;
    
    // Detection Evasion
    public bool RotateUserAgents { get; set; } = false;
    public string? CustomUserAgentsJson { get; set; }
    public bool RandomizeHeaderOrder { get; set; } = false;
    public bool UseRandomJitter { get; set; } = false;
    public int MinJitterMs { get; set; } = 0;
    public int MaxJitterMs { get; set; } = 1000;
    public bool SpoofReferer { get; set; } = false;
    public string? CustomHeadersJson { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
