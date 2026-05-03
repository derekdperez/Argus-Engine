namespace ArgusEngine.Domain.Entities;

public sealed class Ec2WorkerMachine
{
    public string InstanceId { get; set; } = "";
    public string WorkerKind { get; set; } = "";
    public string State { get; set; } = "";
    public string? PublicIp { get; set; }
    public string? PrivateIp { get; set; }
    public string? LaunchTemplateId { get; set; }
    public string? LaunchTemplateVersion { get; set; }
    public DateTimeOffset? LaunchedAtUtc { get; set; }
    public DateTimeOffset? TerminatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
