using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ArgusEngine.Infrastructure.Data;

public static class ArgusDbBootstrap
{
    private const long SchemaBootstrapAdvisoryLockKey = 542017296183746291L;
    private const int MaxBootstrapAttempts = 5;

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

    private static readonly Action<ILogger, string, int, int, int, Exception?> LogStartupDatabaseBootstrapRetry =
        LoggerMessage.Define<string, int, int, int>(
            LogLevel.Warning,
            new EventId(4, nameof(LogStartupDatabaseBootstrapRetry)),
            "Startup database bootstrap hit transient PostgreSQL error {SqlState}; retrying attempt {Attempt}/{MaxAttempts} after {DelayMs}ms.");

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapUnlockFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(5, nameof(LogStartupDatabaseBootstrapUnlockFailed)),
            "Failed to release the startup database bootstrap advisory lock. The connection close will release any remaining session-level advisory locks.");

    public static Task InitializeAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        bool includeFileStore,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithBootstrapRetriesAsync(
            () => InitializeOnceAsync(services, configuration, logger, includeFileStore, cancellationToken),
            logger,
            cancellationToken);
    }

    private static async Task InitializeOnceAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        bool includeFileStore,
        CancellationToken cancellationToken)
    {
        if (ShouldSkipStartupDatabase(configuration))
        {
            LogStartupDatabaseBootstrapSkipped(logger, null);
            return;
        }

        var mode = (configuration["Argus:Database:BootstrapMode"] ?? "EnsureCreated").Trim();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var advisoryLockHeld = false;
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_lock({SchemaBootstrapAdvisoryLockKey});",
                    cancellationToken)
                .ConfigureAwait(false);

            advisoryLockHeld = true;

            if (mode.Equals("Migrate", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await ArgusDbSeeder.SeedWorkerSwitchesAsync(db, cancellationToken).ConfigureAwait(false);

                if (includeFileStore)
                {
                    var fileStoreFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ArgusFileStoreDbContext>>();
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
                var fileStoreFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ArgusFileStoreDbContext>>();
                await using var fs = await fileStoreFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await fs.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            }

            LogStartupDatabaseBootstrapEnsureCreated(logger, null);
        }
        finally
        {
            if (advisoryLockHeld)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                            $"SELECT pg_advisory_unlock({SchemaBootstrapAdvisoryLockKey});",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogStartupDatabaseBootstrapUnlockFailed(logger, ex);
                }
            }

            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task ExecuteWithBootstrapRetriesAsync(
        Func<Task> operation,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex) when (
                attempt < MaxBootstrapAttempts &&
                IsRetryableBootstrapException(ex) &&
                !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(1000 * attempt);
                LogStartupDatabaseBootstrapRetry(
                    logger,
                    ex.SqlState,
                    attempt,
                    MaxBootstrapAttempts,
                    (int)delay.TotalMilliseconds,
                    ex);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableBootstrapException(PostgresException exception)
    {
        return exception.SqlState is
            PostgresErrorCodes.DeadlockDetected or
            PostgresErrorCodes.LockNotAvailable or
            PostgresErrorCodes.SerializationFailure;
    }

    private static bool ShouldSkipStartupDatabase(IConfiguration configuration)
    {
        var configuredSkip = configuration["Argus:SkipStartupDatabase"] ?? configuration["ARGUS_SKIP_STARTUP_DATABASE"];

        if (string.Equals(configuredSkip, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuredSkip, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }
}
