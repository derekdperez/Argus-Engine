namespace ArgusEngine.Workers.HttpRequester;

public sealed record HttpRequesterOptions
{
    public int MaxConcurrency { get; init; } = 10;

    public int VisibilityTimeoutSeconds { get; init; } = 60;

    public int PollIntervalSeconds { get; init; } = 1;

    public bool AllowInsecureSsl { get; init; } = true;

    public string UserAgent { get; init; } = "ArgusEngine.HttpRequester/1.0";
}
