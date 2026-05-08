using ArgusEngine.CommandCenter.Realtime.Host.Hubs;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Infrastructure.Observability;
using Microsoft.AspNetCore.SignalR;
using DiscoveryHubEvents = ArgusEngine.CommandCenter.Contracts.DiscoveryHubEvents;

namespace ArgusEngine.CommandCenter.Realtime.Host.Services;

public interface IRealtimeUpdatePublisher
{
    Task PublishAsync(
        string scope,
        string kind,
        Guid? targetId,
        Guid? assetId,
        string summary,
        CancellationToken cancellationToken = default);

    Task PublishStatusAsync(CommandCenterStatusSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class SignalRRealtimeUpdatePublisher(IHubContext<DiscoveryHub> hubContext) : IRealtimeUpdatePublisher
{
    public Task PublishDomainEventAsync(LiveUiEventDto evt, CancellationToken cancellationToken = default)
    {
        ArgusMeters.RealtimeUiEvents.Add(
            1,
            new KeyValuePair<string, object?>("scope", evt.Scope),
            new KeyValuePair<string, object?>("kind", evt.Kind));

        return hubContext.Clients.All
            .SendAsync(DiscoveryHubEvents.DomainEvent, evt, cancellationToken);
    }

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

