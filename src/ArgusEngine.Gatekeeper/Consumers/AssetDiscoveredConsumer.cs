using MassTransit;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Gatekeeper.Consumers;

public sealed class AssetDiscoveredConsumer(
    IInboxDeduplicator inbox,
    GatekeeperOrchestrator orchestrator) : IConsumer<AssetDiscovered>
{
    public async Task Consume(ConsumeContext<AssetDiscovered> context)
    {
        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(AssetDiscoveredConsumer), context.CancellationToken).ConfigureAwait(false))
            return;

        await orchestrator.ProcessAsync(context.Message, context.CancellationToken).ConfigureAwait(false);
    }
}
