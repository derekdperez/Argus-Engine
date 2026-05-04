using ArgusEngine.CommandCenter.Models;

namespace ArgusEngine.CommandCenter.Realtime;

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
