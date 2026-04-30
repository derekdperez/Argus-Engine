using NightmareV2.Domain.Entities;

namespace NightmareV2.Application.Workers;

public interface IHttpRequestQueueStateMachine
{
    bool CanTransition(HttpRequestQueueStateKind from, HttpRequestQueueStateKind toKind);
}
