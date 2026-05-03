namespace ArgusEngine.Domain.Entities;

public static class HttpRequestQueueState
{
    public const string Queued = "Queued";
    public const string InFlight = "InFlight";
    public const string Succeeded = "Succeeded";
    public const string Retry = "Retry";
    public const string Failed = "Failed";

    public static HttpRequestQueueStateKind ToKind(string state) =>
        state switch
        {
            Queued => HttpRequestQueueStateKind.Queued,
            InFlight => HttpRequestQueueStateKind.InFlight,
            Succeeded => HttpRequestQueueStateKind.Succeeded,
            Retry => HttpRequestQueueStateKind.Retry,
            Failed => HttpRequestQueueStateKind.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown HTTP request queue state."),
        };

    public static string FromKind(HttpRequestQueueStateKind kind) =>
        kind switch
        {
            HttpRequestQueueStateKind.Queued => Queued,
            HttpRequestQueueStateKind.InFlight => InFlight,
            HttpRequestQueueStateKind.Succeeded => Succeeded,
            HttpRequestQueueStateKind.Retry => Retry,
            HttpRequestQueueStateKind.Failed => Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown HTTP request queue state kind."),
        };
}
