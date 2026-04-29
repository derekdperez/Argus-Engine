namespace NightmareV2.Application.Workers;

public sealed class SubdomainEnumerationResult
{
    public required string Hostname { get; init; }
    public required string Provider { get; init; }
    public required string Method { get; init; }
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
}
