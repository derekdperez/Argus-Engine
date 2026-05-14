namespace ArgusEngine.CommandCenter.WorkerControl.Api.Services;

public sealed class CoverageAutomationOptions
{
    public bool Enabled { get; init; } = true;
    public int InitialDelaySeconds { get; init; } = 15;
    public int IntervalSeconds { get; init; } = 30;
    public int EnumerationBatchSize { get; init; } = 25;
    public int SpiderBatchSize { get; init; } = 500;
    public int EnumerationRetryMinutes { get; init; } = 180;
    public bool EnsureWorkersAvailable { get; init; } = true;
    public int EnumerationWorkerMinimumCount { get; init; } = 1;
    public int SpiderWorkerMinimumCount { get; init; } = 1;
    public int HttpRequesterWorkerMinimumCount { get; init; } = 1;
}
