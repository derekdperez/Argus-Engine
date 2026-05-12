using System.Data;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);

using var host = builder.Build();

var configuration = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CommandCenterBootstrapper");

// Local hot deploys frequently re-run this one-shot service even when the
// database is already initialized. On large existing databases the legacy
// EnsureCreated compatibility patches/backfills can be expensive and have
// caused the container to terminate with exit 139 during compose reconciliation.
// Skip the full bootstrap when the core schema is already present. For a fresh
// install, or when an explicit schema backfill is desired, set
// ARGUS_FORCE_BOOTSTRAP=1.
if (!ForceBootstrap(configuration) &&
    await LooksAlreadyBootstrappedAsync(host.Services, logger, CancellationToken.None).ConfigureAwait(false))
{
    logger.LogInformation(
        "Command Center bootstrapper skipped because the Argus database already contains the core schema. Set ARGUS_FORCE_BOOTSTRAP=1 to force a full bootstrap.");
    return;
}

await ArgusDbBootstrap.InitializeAsync(
        host.Services,
        configuration,
        logger,
        includeFileStore: true,
        CancellationToken.None)
    .ConfigureAwait(false);

static bool ForceBootstrap(IConfiguration configuration)
{
    var configured =
        configuration["Argus:ForceBootstrap"] ??
        configuration["ARGUS_FORCE_BOOTSTRAP"] ??
        Environment.GetEnvironmentVariable("ARGUS_FORCE_BOOTSTRAP");

    return string.Equals(configured, "1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(configured, "yes", StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> LooksAlreadyBootstrappedAsync(
    IServiceProvider services,
    ILogger logger,
    CancellationToken cancellationToken)
{
    try
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = """
                SELECT
                    to_regclass('public.stored_assets') IS NOT NULL
                    AND to_regclass('public.http_request_queue') IS NOT NULL
                    AND to_regclass('public.worker_switches') IS NOT NULL;
                """;

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is bool ready && ready;
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not determine whether the Argus database is already bootstrapped; running full bootstrap.");
        return false;
    }
}
