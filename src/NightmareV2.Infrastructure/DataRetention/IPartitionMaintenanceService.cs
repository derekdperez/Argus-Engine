namespace NightmareV2.Infrastructure.DataRetention;

public interface IPartitionMaintenanceService
{
    Task EnsurePartitionsAsync(CancellationToken ct);
}
