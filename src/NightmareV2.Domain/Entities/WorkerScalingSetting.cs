namespace NightmareV2.Domain.Entities;

public sealed class WorkerScalingSetting
{
    public string ScaleKey { get; set; } = "";
    public int MinTasks { get; set; }
    public int MaxTasks { get; set; }
    public int TargetBacklogPerTask { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
