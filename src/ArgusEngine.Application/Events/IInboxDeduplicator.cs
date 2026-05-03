using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Application.Events;

public interface IInboxDeduplicator
{
    Task<bool> TryBeginProcessingAsync(
        IEventEnvelope envelope,
        string consumer,
        CancellationToken cancellationToken = default);
}
