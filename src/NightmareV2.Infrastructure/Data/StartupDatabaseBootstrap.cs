using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightmareV2.Infrastructure.Data;

public static class StartupDatabaseBootstrap
{
    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapSkipped =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogStartupDatabaseBootstrapSkipped)),
            "Startup database bootstrap skipped (Nightmare:SkipStartupDatabase or NIGHTMARE_SKIP_STARTUP_DATABASE=1).");
    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapMigrated =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(LogStartupDatabaseBootstrapMigrated)),
            "Startup database bootstrap completed via Migrate mode.");
    private static readonly Action<ILogger, Exception?> LogStartupDatabaseBootstrapEnsureCreated =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(LogStartupDatabaseBootstrapEnsureCreated)),
            "Startup database bootstrap used EnsureCreated compatibility mode. Set Nightmare:Database:BootstrapMode=Migrate after adding migrations.");

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

        var mode = (configuration["Nightmare:Database:BootstrapMode"] ?? "EnsureCreated").Trim();
        using var scope = services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<NightmareDbContext>();
        if (mode.Equals("Migrate", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await NightmareDbSeeder.SeedWorkerSwitchesAsync(db, cancellationToken).ConfigureAwait(false);
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
        await NightmareDbSchemaPatches.ApplyAfterEnsureCreatedAsync(db, cancellationToken).ConfigureAwait(false);
        await NightmareDbSeeder.SeedWorkerSwitchesAsync(db, cancellationToken).ConfigureAwait(false);
        if (includeFileStore)
        {
            var fileStoreFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FileStoreDbContext>>();
            await using var fs = await fileStoreFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await fs.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        LogStartupDatabaseBootstrapEnsureCreated(logger, null);
    }

    private static bool ShouldSkipStartupDatabase(IConfiguration configuration)
    {
        var configuredSkip = configuration["Nightmare:SkipStartupDatabase"] ?? configuration["NIGHTMARE_SKIP_STARTUP_DATABASE"];
        if (string.Equals(configuredSkip, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredSkip, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }
}
