namespace ArgusEngine.Domain.Entities;

public sealed class BusJournalEntry
{
    public Guid Id { get; set; }
    public string Direction { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string? PayloadJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
