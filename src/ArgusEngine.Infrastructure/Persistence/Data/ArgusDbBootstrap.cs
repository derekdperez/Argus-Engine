using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Data;

public static class ArgusDbBootstrap
{
    private const string StartupBootstrapAdvisoryLockSql = "SELECT pg_advisory_lock(542017296183746291);";
    private const string StartupBootstrapAdvisoryUnlockSql = "SELECT pg_advisory_unlock(542017296183746291);";

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapSkipped =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogStartupDatabaseBootstrapSkipped)),
            "Startup database bootstrap skipped (Argus:SkipStartupDatabase or ARGUS_SKIP_STARTUP_DATABASE=1).");

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapMigrated =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(LogStartupDatabaseBootstrapMigrated)),
            "Startup database bootstrap completed via Migrate mode.");

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapEnsureCreated =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(LogStartupDatabaseBootstrapEnsureCreated)),
            "Startup database bootstrap used EnsureCreated compatibility mode. Set Argus:Database:BootstrapMode=Migrate after adding migrations.");

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapLockWaiting =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(4, nameof(LogStartupDatabaseBootstrapLockWaiting)),
            "Waiting for startup database bootstrap advisory lock.");

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapLockAcquired =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(5, nameof(LogStartupDatabaseBootstrapLockAcquired)),
            "Startup database bootstrap advisory lock acquired.");

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapLockReleased =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(6, nameof(LogStartupDatabaseBootstrapLockReleased)),
            "Startup database bootstrap advisory lock released.");

    public static async Task InitializeAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        bool includeFileStore,
        CancellationToken cancellationToken = default)
    {
        if (ShouldSkipStartupDatabase(configuration))
        {
            LogStartupDatabaseBootstrapSkipped(logger, null);
            return;
        }

        var mode = (configuration["Argus:Database:BootstrapMode"] ?? "EnsureCreated").Trim();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

        await AcquireStartupBootstrapLockAsync(db, logger, cancellationToken).ConfigureAwait(false);

        try
        {
            if (mode.Equals("Migrate", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

                await ArgusDbSeeder.SeedWorkerSwitchesAsync(db, cancellationToken).ConfigureAwait(false);

                if (includeFileStore)
                {
                    var fileStoreFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FileStoreDbContext>>();
                    await using var fs = await fileStoreFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    await fs.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                }

                LogStartupDatabaseBootstrapMigrated(logger, null);
                return;
            }

            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

            await ArgusDbSchemaPatches.ApplyAfterEnsureCreatedAsync(db, logger, cancellationToken).ConfigureAwait(false);

            await ArgusDbSeeder.SeedWorkerSwitchesAsync(db, cancellationToken).ConfigureAwait(false);

            if (includeFileStore)
            {
                var fileStoreFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FileStoreDbContext>>();
                await using var fs = await fileStoreFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

                await fs.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            }

            LogStartupDatabaseBootstrapEnsureCreated(logger, null);
        }
        finally
        {
            await ReleaseStartupBootstrapLockAsync(db, logger).ConfigureAwait(false);
        }
    }

    private static async Task AcquireStartupBootstrapLockAsync(
        ArgusDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            LogStartupDatabaseBootstrapLockWaiting(logger, null);

            await db.Database.ExecuteSqlRawAsync(StartupBootstrapAdvisoryLockSql, cancellationToken).ConfigureAwait(false);

            LogStartupDatabaseBootstrapLockAcquired(logger, null);
        }
        catch
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ReleaseStartupBootstrapLockAsync(ArgusDbContext db, ILogger logger)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(StartupBootstrapAdvisoryUnlockSql, CancellationToken.None).ConfigureAwait(false);

            LogStartupDatabaseBootstrapLockReleased(logger, null);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static bool ShouldSkipStartupDatabase(IConfiguration configuration)
    {
        var configuredSkip = configuration["Argus:SkipStartupDatabase"] ?? configuration["ARGUS_SKIP_STARTUP_DATABASE"];

        if (string.Equals(configuredSkip, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredSkip, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }
}
