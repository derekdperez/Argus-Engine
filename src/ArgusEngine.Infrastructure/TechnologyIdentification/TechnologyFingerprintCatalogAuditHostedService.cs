using ArgusEngine.Application.TechnologyIdentification.Fingerprints;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.TechnologyIdentification;

public sealed partial class TechnologyFingerprintCatalogAuditHostedService(
    ITechnologyFingerprintCatalog catalog,
    IDbContextFactory<ArgusDbContext> dbContextFactory,
    IHostEnvironment environment,
    ILogger<TechnologyFingerprintCatalogAuditHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var resourcePath = catalog is ResourceTechnologyFingerprintCatalog resourceCatalog
            ? resourceCatalog.ResourcePath
            : Path.Combine("Resources", "TechnologyDetection", "argus_fingerprints.json");

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO technology_catalog_loads (
                        id,
                        catalog_hash,
                        fingerprint_count,
                        resource_path,
                        loaded_by_service,
                        loaded_at_utc,
                        validation_status,
                        validation_errors_json
                    )
                    VALUES (
                        {Guid.NewGuid()},
                        {catalog.CatalogHash},
                        {catalog.Fingerprints.Count},
                        {resourcePath},
                        {environment.ApplicationName},
                        {DateTimeOffset.UtcNow},
                        {"valid"},
                        '[]'::jsonb
                    );
                    """,
                    cancellationToken)
                .ConfigureAwait(false);

            LogCatalogLoadAudited(logger, catalog.CatalogHash, catalog.Fingerprints.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCatalogLoadAuditFailed(logger, ex, catalog.CatalogHash);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 550010,
        Level = LogLevel.Information,
        Message = "Audited technology fingerprint catalog load. Hash={Hash}, Count={Count}")]
    private static partial void LogCatalogLoadAudited(ILogger logger, string hash, int count);

    [LoggerMessage(
        EventId = 550011,
        Level = LogLevel.Warning,
        Message = "Failed to audit technology fingerprint catalog load. Hash={Hash}")]
    private static partial void LogCatalogLoadAuditFailed(ILogger logger, Exception exception, string hash);
}
