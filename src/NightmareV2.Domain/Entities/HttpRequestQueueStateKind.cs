namespace NightmareV2.Domain.Entities;

public enum HttpRequestQueueStateKind
{
    Queued = 0,
    InFlight = 1,
    Succeeded = 2,
    Retry = 3,
    Failed = 4,
}
