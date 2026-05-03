using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Persistence.Data;
using Npgsql;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class EfAssetPersistence(
    ArgusDbContext db,
    IEventOutbox outbox,
    IAssetGraphService graph,
    ILogger<EfAssetPersistence> logger) : IAssetPersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly Action<ILogger, Guid, Exception?> LogHttpRequestQueueAlreadyExists =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(1, nameof(LogHttpRequestQueueAlreadyExists)),
            "HTTP request queue row already exists for asset {AssetId}.");
    private static readonly Action<ILogger, Guid, int, Exception?> LogUrlAssetSoft404 =
        LoggerMessage.Define<Guid, int>(
            LogLevel.Debug,
            new EventId(2, nameof(LogUrlAssetSoft404)),
            "URL asset {AssetId} returned HTTP {StatusCode} but response body looks like a 404/not-found error; leaving unconfirmed.");
    private static readonly Action<ILogger, int, Guid, Exception?> LogScannableContentPublishRetryFailed =
        LoggerMessage.Define<int, Guid>(
            LogLevel.Warning,
            new EventId(3, nameof(LogScannableContentPublishRetryFailed)),
            "ScannableContentAvailable publish retry {Attempt} failed for asset {AssetId}.");
    private static readonly TimeSpan[] PublishRetryDelays =
    [
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(750),
    ];

    public async Task<(Guid AssetId, bool Inserted)> PersistNewAssetAsync(
        AssetDiscovered message,
        CanonicalAsset canonical,
        CancellationToken cancellationToken = default)
    {
        var result = await graph.UpsertAssetAsync(message, canonical, cancellationToken).ConfigureAwait(false);
        if (result.SkippedReason is not null)
            return (Guid.Empty, false);

        return (result.AssetId, result.Inserted);
    }

    private void DetachPendingAssetGraph(StoredAsset asset)
    {
        db.Entry(asset).State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<HttpRequestQueueItem>()
                     .Where(e => e.Entity.AssetId == asset.Id && e.State == EntityState.Added))
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task EnsureQueuedAssetHasHttpRequestAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null || asset.LifecycleStatus != AssetLifecycleStatus.Queued)
            return;

        var hasQueueItem = await db.HttpRequestQueue.AsNoTracking()
            .AnyAsync(q => q.AssetId == assetId, cancellationToken)
            .ConfigureAwait(false);
        if (hasQueueItem)
            return;

        EnqueueHttpRequestIfNeeded(asset);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName?.Contains("http_request_queue", StringComparison.OrdinalIgnoreCase) == true)
        {
            LogHttpRequestQueueAlreadyExists(logger, assetId, ex);
        }
    }

    private static string ResolveInitialLifecycleStatus(AssetDiscovered message)
    {
        return AssetLifecycleStatus.Queued;
    }

    private void EnqueueHttpRequestIfNeeded(StoredAsset asset)
    {
        if (!ShouldRequest(asset.Kind))
            return;

        if (!TryResolveRequestUrl(asset, out var requestUrl, out var domainKey))
            return;

        db.HttpRequestQueue.Add(
            new HttpRequestQueueItem
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                TargetId = asset.TargetId,
                AssetKind = asset.Kind,
                Method = "GET",
                RequestUrl = requestUrl,
                DomainKey = domainKey,
                State = HttpRequestQueueState.Queued,
                Priority = ResolvePriority(asset.Kind),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = DateTimeOffset.UtcNow,
            });
    }

    private static bool ShouldRequest(AssetKind kind) =>
        kind is AssetKind.Url or AssetKind.ApiEndpoint or AssetKind.JavaScriptFile or AssetKind.MarkdownBody
            or AssetKind.Subdomain or AssetKind.Domain;

    private static int ResolvePriority(AssetKind kind) =>
        kind is AssetKind.Subdomain or AssetKind.Domain ? 10 : 0;

    private static bool TryResolveRequestUrl(StoredAsset asset, out string requestUrl, out string domainKey)
    {
        requestUrl = "";
        domainKey = "";

        if (asset.Kind is AssetKind.Subdomain or AssetKind.Domain)
        {
            var host = asset.RawValue.Trim().TrimEnd('/');
            if (host.Length == 0)
                return false;
            if (!Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var domainUri))
                return false;
            requestUrl = domainUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            domainKey = domainUri.IdnHost.ToLowerInvariant();
            return true;
        }

        var raw = asset.RawValue.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + raw, UriKind.Absolute, out uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        requestUrl = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        domainKey = uri.IdnHost.ToLowerInvariant();
        return true;
    }
}
