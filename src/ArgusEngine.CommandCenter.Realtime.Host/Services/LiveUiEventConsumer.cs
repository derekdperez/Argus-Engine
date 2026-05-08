using ArgusEngine.CommandCenter.Models;
using MassTransit;

namespace ArgusEngine.CommandCenter.Realtime.Host.Services;

public sealed class LiveUiEventConsumer(SignalRRealtimeUpdatePublisher publisher) : IConsumer<LiveUiEventDto>
{
    public Task Consume(ConsumeContext<LiveUiEventDto> context) =>
        publisher.PublishDomainEventAsync(context.Message, context.CancellationToken);
}
