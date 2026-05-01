using NightmareV2.Application.Events;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Workers.Enum.Tests;

internal sealed class CapturingEventOutbox : IEventOutbox
{
    public List<IEventEnvelope> Messages { get; } = [];

    public Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default) where TEvent : class, IEventEnvelope
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
