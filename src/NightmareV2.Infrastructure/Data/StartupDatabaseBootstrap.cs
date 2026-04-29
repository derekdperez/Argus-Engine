using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightmareV2.Infrastructure.Data;

public static class StartupDatabaseBootstrap
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        bool includeFileStore,
        CancellationToken cancellationToken = default)
    {
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

            logger.LogInformation("Startup database bootstrap completed via Migrate mode.");
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

        logger.LogWarning(
            "Startup database bootstrap used EnsureCreated compatibility mode. Set Nightmare:Database:BootstrapMode=Migrate after adding migrations.");
    }
}
