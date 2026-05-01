using NightmareV2.Application.Workers;
using NightmareV2.Domain.Entities;

namespace NightmareV2.Infrastructure.Workers;

public sealed class DefaultHttpRequestQueueStateMachine : IHttpRequestQueueStateMachine
{
    public bool CanTransition(HttpRequestQueueStateKind from, HttpRequestQueueStateKind toKind)
    {
        if (from == toKind)
            return true;

        return from switch
        {
            HttpRequestQueueStateKind.Queued => toKind is HttpRequestQueueStateKind.InFlight or HttpRequestQueueStateKind.Failed,
            HttpRequestQueueStateKind.InFlight => toKind is HttpRequestQueueStateKind.Succeeded or HttpRequestQueueStateKind.Retry or HttpRequestQueueStateKind.Failed,
            HttpRequestQueueStateKind.Retry => toKind is HttpRequestQueueStateKind.InFlight or HttpRequestQueueStateKind.Failed,
            HttpRequestQueueStateKind.Succeeded => false,
            HttpRequestQueueStateKind.Failed => false,
            _ => false,
        };
    }
}
