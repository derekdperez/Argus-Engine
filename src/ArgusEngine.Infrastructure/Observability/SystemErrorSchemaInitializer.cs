using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Observability;

/// <summary>
/// Ensures the centralized system error log table exists before the development diagnostics page
/// or the database logger attempts to read or write it. The logger also performs this check before
/// writes, so this initializer is intentionally best-effort and non-fatal.
/// </summary>
public sealed class SystemErrorSchemaInitializer(
    IServiceProvider serviceProvider,
    ILogger<SystemErrorSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<ArgusDbContext>>();
            if (dbFactory is null)
            {
                logger.LogDebug(
                    "Skipping system error log schema initialization because ArgusDbContext is not registered.");
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await ArgusDatabaseLoggerProvider.EnsureSystemErrorTableAsync(db, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            // Do not escalate to Error here; a failing diagnostics sink must not create a recursive log storm.
            logger.LogDebug(ex, "Failed to initialize the system error log schema.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
