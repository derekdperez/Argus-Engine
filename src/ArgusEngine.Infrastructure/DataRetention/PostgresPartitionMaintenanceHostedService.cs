using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.DataRetention;

public sealed class PostgresPartitionMaintenanceHostedService(
    IServiceProvider services,
    ILogger<PostgresPartitionMaintenanceHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogMaintenanceFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(EnsureOnceAsync)), "Partition maintenance failed.");
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
            LogMaintenanceFailed(logger, ex);
        }
    }
}
