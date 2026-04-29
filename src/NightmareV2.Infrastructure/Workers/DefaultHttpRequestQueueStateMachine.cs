using NightmareV2.Application.Workers;
using NightmareV2.Domain.Entities;

namespace NightmareV2.Infrastructure.Workers;

public sealed class DefaultHttpRequestQueueStateMachine : IHttpRequestQueueStateMachine
{
    public bool CanTransition(HttpRequestQueueStateKind from, HttpRequestQueueStateKind to)
    {
        if (from == to)
            return true;

        return from switch
        {
            HttpRequestQueueStateKind.Queued => to is HttpRequestQueueStateKind.InFlight or HttpRequestQueueStateKind.Failed,
            HttpRequestQueueStateKind.InFlight => to is HttpRequestQueueStateKind.Succeeded or HttpRequestQueueStateKind.Retry or HttpRequestQueueStateKind.Failed,
            HttpRequestQueueStateKind.Retry => to is HttpRequestQueueStateKind.InFlight or HttpRequestQueueStateKind.Failed,
            HttpRequestQueueStateKind.Succeeded => false,
            HttpRequestQueueStateKind.Failed => false,
            _ => false,
        };
    }
}
