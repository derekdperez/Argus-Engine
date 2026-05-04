using ArgusEngine.CommandCenter.Hubs;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Infrastructure.Observability;
using Microsoft.AspNetCore.SignalR;

namespace ArgusEngine.CommandCenter.Realtime;

public sealed class SignalRRealtimeUpdatePublisher(IHubContext<DiscoveryHub> hubContext) : IRealtimeUpdatePublisher
{
    public async Task PublishAsync(
        string scope,
        string kind,
        Guid? targetId,
        Guid? assetId,
        string summary,
        CancellationToken cancellationToken = default)
    {
        var evt = new LiveUiEventDto(
            kind,
            targetId,
            assetId,
            scope,
            summary,
            DateTimeOffset.UtcNow);

        ArgusMeters.RealtimeUiEvents.Add(
            1,
            new KeyValuePair<string, object?>("scope", scope),
            new KeyValuePair<string, object?>("kind", kind));

        await hubContext.Clients.All
            .SendAsync(DiscoveryHubEvents.DomainEvent, evt, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishStatusAsync(CommandCenterStatusSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgusMeters.RealtimeUiEvents.Add(
            1,
            new KeyValuePair<string, object?>("scope", "status"),
            new KeyValuePair<string, object?>("kind", DiscoveryHubEvents.StatusChanged));

        await hubContext.Clients.All
            .SendAsync(DiscoveryHubEvents.StatusChanged, snapshot, cancellationToken)
            .ConfigureAwait(false);
    }
}
