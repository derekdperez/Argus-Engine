using MassTransit;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Gatekeeper.Consumers;

public sealed class AssetRelationshipDiscoveredConsumer(
    IAssetGraphService graph,
    IInboxDeduplicator inbox) : IConsumer<AssetRelationshipDiscovered>
{
    public async Task Consume(ConsumeContext<AssetRelationshipDiscovered> context)
    {
        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(AssetRelationshipDiscoveredConsumer), context.CancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        await graph.UpsertRelationshipAsync(context.Message, context.CancellationToken).ConfigureAwait(false);
    }
}
