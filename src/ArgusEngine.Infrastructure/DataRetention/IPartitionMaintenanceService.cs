namespace ArgusEngine.Infrastructure.DataRetention;

public interface IPartitionMaintenanceService
{
    Task EnsurePartitionsAsync(CancellationToken ct);
}
