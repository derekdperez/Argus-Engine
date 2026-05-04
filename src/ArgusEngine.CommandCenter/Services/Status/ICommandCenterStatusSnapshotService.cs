using ArgusEngine.CommandCenter.Models;

namespace ArgusEngine.CommandCenter.Services.Status;

public interface ICommandCenterStatusSnapshotService
{
    Task<CommandCenterStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
