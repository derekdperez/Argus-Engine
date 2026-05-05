using System.ComponentModel.DataAnnotations;

namespace ArgusEngine.Domain.Entities;

public sealed class WorkerHeartbeat
{
    [Key]
    public string HostName { get; set; } = "";
    public string WorkerKey { get; set; } = "";
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public int ActiveConsumerCount { get; set; }
    public int ProcessId { get; set; }
    public string? Version { get; set; }
    public bool IsHealthy { get; set; } = true;
    public string? HealthMessage { get; set; }
}
