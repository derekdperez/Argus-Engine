namespace ArgusEngine.Domain.Entities;

public sealed class WorkerScaleTarget
{
    public string ScaleKey { get; set; } = "";
    public int DesiredCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
