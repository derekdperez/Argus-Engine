using ArgusEngine.Domain.Entities;

namespace ArgusEngine.Application.Workers;

public interface IHttpRequestQueueStateMachine
{
    bool CanTransition(HttpRequestQueueStateKind from, HttpRequestQueueStateKind toKind);
}
