namespace ArgusEngine.Workers.Spider;

public sealed class SpiderHttpOptions
{
    public bool AllowInsecureSsl { get; set; }
    public int MaxConcurrency { get; set; } = 50;
    public int VisibilityTimeoutSeconds { get; set; } = 300;
    public int PollIntervalSeconds { get; set; } = 5;
}
