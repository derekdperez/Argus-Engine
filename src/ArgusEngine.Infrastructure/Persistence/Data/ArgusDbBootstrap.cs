using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ArgusEngine.Infrastructure.Data;

public static class ArgusDbBootstrap
{
    private const long BootstrapAdvisoryLockKey = 542017296183746291L;
    private const int MaxBootstrapAttempts = 3;

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

    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapMigrateFallback =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4, nameof(LogStartupDatabaseBootstrapMigrateFallback)),
            "Startup database bootstrap requested Migrate mode, but no EF migrations were found. Falling back to EnsureCreated compatibility mode.");

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

        await ExecuteWithBootstrapRetryAsync(
                db,
                logger,
                async () =>
                {
                    if (mode.Equals("Migrate", StringComparison.OrdinalIgnoreCase))
                    {
                        var migrations = db.Database.GetMigrations();
                        if (!migrations.Any())
                        {
                            LogStartupDatabaseBootstrapMigrateFallback(logger, null);
                            await BootstrapEnsureCreatedAsync(scope.ServiceProvider, db, logger, includeFileStore, cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }

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

                    await BootstrapEnsureCreatedAsync(scope.ServiceProvider, db, logger, includeFileStore, cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteWithBootstrapRetryAsync(
        ArgusDbContext db,
        ILogger logger,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteWithBootstrapLockAsync(db, logger, operation, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex) when (IsTransientBootstrapFailure(ex) && attempt < MaxBootstrapAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Transient PostgreSQL bootstrap failure {SqlState} on attempt {Attempt}/{MaxAttempts}. Retrying.",
                    ex.SqlState,
                    attempt,
                    MaxBootstrapAttempts);

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteWithBootstrapLockAsync(
        ArgusDbContext db,
        ILogger logger,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        if (closeConnection)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_lock({BootstrapAdvisoryLockKey});",
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await operation().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                            $"SELECT pg_advisory_unlock({BootstrapAdvisoryLockKey});",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release PostgreSQL bootstrap advisory lock.");
                }
            }
        }
        finally
        {
            if (closeConnection)
            {
                db.Database.CloseConnection();
            }
        }
    }

    private static bool IsTransientBootstrapFailure(PostgresException ex)
    {
        return string.Equals(ex.SqlState, PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal)
            || string.Equals(ex.SqlState, PostgresErrorCodes.LockNotAvailable, StringComparison.Ordinal)
            || string.Equals(ex.SqlState, PostgresErrorCodes.SerializationFailure, StringComparison.Ordinal);
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

    private static async Task BootstrapEnsureCreatedAsync(
        IServiceProvider services,
        ArgusDbContext db,
        ILogger logger,
        bool includeFileStore,
        CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        await ArgusDbSchemaPatches.ApplyAfterEnsureCreatedAsync(db, logger, cancellationToken).ConfigureAwait(false);

        await ArgusDbSeeder.SeedWorkerSwitchesAsync(db, cancellationToken).ConfigureAwait(false);

        if (includeFileStore)
        {
            var fileStoreFactory = services.GetRequiredService<IDbContextFactory<FileStoreDbContext>>();
            await using var fs = await fileStoreFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            await fs.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        LogStartupDatabaseBootstrapEnsureCreated(logger, null);
    }
}
