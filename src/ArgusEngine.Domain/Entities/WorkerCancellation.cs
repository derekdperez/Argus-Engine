using System;

namespace ArgusEngine.Domain.Entities;

public class WorkerCancellation
{
    public Guid MessageId { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public string? Reason { get; set; }
}
