namespace NightmareV2.Domain.Entities;

public sealed class Ec2WorkerMachine
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? InstanceId { get; set; }
    public string AwsState { get; set; } = "";
    public string? PublicIpAddress { get; set; }
    public string? PrivateIpAddress { get; set; }
    public string? InstanceType { get; set; }
    public string? LastCommandId { get; set; }
    public string? LastCommandStatus { get; set; }
    public string? StatusMessage { get; set; }
    public int SpiderWorkers { get; set; }
    public int EnumWorkers { get; set; }
    public int PortScanWorkers { get; set; }
    public int HighValueWorkers { get; set; }
    public int TechnologyIdentificationWorkers { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
}
