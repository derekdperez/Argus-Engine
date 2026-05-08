using Microsoft.AspNetCore.SignalR;

namespace ArgusEngine.CommandCenter.Realtime.Host.Hubs;

/// <summary>Real-time discovery channel (design §4.1 telemetry).</summary>
public sealed class DiscoveryHub : Hub
{
    public Task SubscribeTarget(Guid targetId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, targetId.ToString("N"));

    public Task UnsubscribeTarget(Guid targetId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, targetId.ToString("N"));
}

