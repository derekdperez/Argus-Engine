using MassTransit;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Gatekeeper.Consumers;

public sealed class AssetRelationshipDiscoveredConsumer(
    IInboxDeduplicator inbox,
    IAssetGraphService graph,
    ILogger<AssetRelationshipDiscoveredConsumer> logger) : IConsumer<AssetRelationshipDiscovered>
{
    private static readonly Action<ILogger, Guid, Guid, Guid, string, Exception?> LogRelationshipRejected =
        LoggerMessage.Define<Guid, Guid, Guid, string>(
            LogLevel.Debug,
            new EventId(1, nameof(LogRelationshipRejected)),
            "Gatekeeper relationship rejected for target {TargetId}, parent {ParentAssetId}, child {ChildAssetId}: {Reason}");

    public async Task Consume(ConsumeContext<AssetRelationshipDiscovered> context)
    {
        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(AssetRelationshipDiscoveredConsumer), context.CancellationToken).ConfigureAwait(false))
            return;

        var result = await graph.UpsertRelationshipAsync(context.Message, context.CancellationToken).ConfigureAwait(false);
        if (result.RejectedReason is { Length: > 0 } reason)
        {
            LogRelationshipRejected(
                logger,
                context.Message.TargetId,
                context.Message.ParentAssetId,
                context.Message.ChildAssetId,
                reason,
                null);
        }
    }
}
