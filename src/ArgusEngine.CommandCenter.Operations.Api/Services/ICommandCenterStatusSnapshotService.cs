using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.Operations.Api.Services;

public interface ICommandCenterStatusSnapshotService
{
    Task<CommandCenterStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

