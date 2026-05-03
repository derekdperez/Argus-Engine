using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightmareV2.Infrastructure.DataRetention;

public sealed class PostgresPartitionMaintenanceHostedService(
    IServiceProvider services,
    ILogger<PostgresPartitionMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureOnceAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken).ConfigureAwait(false);
            await EnsureOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IPartitionMaintenanceService>();
            await service.EnsurePartitionsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Partition maintenance failed.");
        }
    }
}
