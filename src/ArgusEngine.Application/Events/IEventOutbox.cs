using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Application.Events;

public interface IEventOutbox
{
    Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class, IEventEnvelope;

    Task EnqueueBatchAsync<TEvent>(IEnumerable<TEvent> messages, CancellationToken cancellationToken = default)
        where TEvent : class, IEventEnvelope;
}
