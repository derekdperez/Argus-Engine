using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.Gatekeeping;
using NightmareV2.Application.HighValue;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;
using Npgsql;

namespace NightmareV2.Infrastructure.Gatekeeping;

public sealed class EfAssetPersistence(
    NightmareDbContext db,
    IEventOutbox outbox,
    IAssetGraphService graph,
    ILogger<EfAssetPersistence> logger) : IAssetPersistence
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
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
            logger.LogDebug(ex, "HTTP request queue row already exists for asset {AssetId}.", assetId);
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

    public async Task ConfirmUrlAssetAsync(
        Guid assetId,
        UrlFetchSnapshot snapshot,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        var meta = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.TargetId, a.RawValue, a.DiscoveredBy, a.CanonicalKey })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (meta is null)
            return;

        var finalUrl = ResolveFinalUrl(snapshot.FinalUrl, meta.RawValue);
        var finalCanonicalKey = TryCanonicalizeUrl(finalUrl);
        var redirectsToExistingAsset = !string.IsNullOrWhiteSpace(finalCanonicalKey)
            && !string.Equals(finalCanonicalKey, meta.CanonicalKey, StringComparison.Ordinal)
            && await db.Assets.AsNoTracking()
                .AnyAsync(
                    a => a.TargetId == meta.TargetId
                        && a.Id != assetId
                        && a.CanonicalKey == finalCanonicalKey,
                    cancellationToken)
                .ConfigureAwait(false);

        var isHttpSuccess = snapshot.StatusCode is >= 200 and < 300;
        var isSoft404 = isHttpSuccess && UrlFetchClassifier.LooksLikeSoft404(snapshot);

        if (isSoft404)
        {
            logger.LogDebug(
                "URL asset {AssetId} returned HTTP {StatusCode} but response body looks like a 404/not-found error; leaving unconfirmed.",
                assetId,
                snapshot.StatusCode);
        }

        var isConfirmedResponse = isHttpSuccess
            && !isSoft404
            && !redirectsToExistingAsset
            && HighValueRedirectIsConfirmable(meta.DiscoveredBy, meta.RawValue, snapshot);

        var now = DateTimeOffset.UtcNow;
        var redirectCount = Math.Max(0, snapshot.RedirectCount);
        if (isConfirmedResponse)
        {
            await db.Assets
                .Where(a => a.Id == assetId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(a => a.LifecycleStatus, AssetLifecycleStatus.Confirmed)
                        .SetProperty(a => a.TypeDetailsJson, json)
                        .SetProperty(a => a.FinalUrl, finalUrl)
                        .SetProperty(a => a.RedirectCount, redirectCount)
                        .SetProperty(a => a.LastSeenAtUtc, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await db.Assets
                .Where(a => a.Id == assetId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(a => a.LifecycleStatus, AssetLifecycleStatus.NonExistent)
                        .SetProperty(a => a.TypeDetailsJson, json)
                        .SetProperty(a => a.FinalUrl, finalUrl)
                        .SetProperty(a => a.RedirectCount, redirectCount)
                        .SetProperty(a => a.LastSeenAtUtc, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!isConfirmedResponse)
            return;

        var correlation = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId;
        var causation = correlation;
        var scanUrl = string.IsNullOrWhiteSpace(finalUrl) ? meta.RawValue ?? "" : finalUrl;

        async Task DelayAsync(int failedAttempt)
        {
            if (failedAttempt >= PublishRetryDelays.Length)
                return;
            await Task.Delay(PublishRetryDelays[failedAttempt], cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; attempt <= PublishRetryDelays.Length + 1; attempt++)
        {
            try
            {
                await outbox.EnqueueAsync(
                        new ScannableContentAvailable(
                            assetId,
                            meta.TargetId,
                            scanUrl,
                            correlation,
                            DateTimeOffset.UtcNow,
                            ScannableContentSource.UrlHttpResponse,
                            EventId: Guid.NewGuid(),
                            CausationId: causation,
                            Producer: "gatekeeper"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt <= PublishRetryDelays.Length)
            {
                logger.LogWarning(
                    ex,
                    "ScannableContentAvailable publish retry {Attempt} failed for asset {AssetId}.",
                    attempt,
                    assetId);
                await DelayAsync(attempt - 1).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Failed to publish ScannableContentAvailable for asset {assetId} after retries.");
    }

    private static string? ResolveFinalUrl(string? finalUrl, string? fallbackRaw)
    {
        var candidate = string.IsNullOrWhiteSpace(finalUrl) ? fallbackRaw : finalUrl;
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
            return candidate.Trim();
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return candidate.Trim();
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    private static bool HighValueRedirectIsConfirmable(string discoveredBy, string rawUrl, UrlFetchSnapshot snapshot)
    {
        if (!discoveredBy.StartsWith("hvpath:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (snapshot.RedirectCount <= 0)
            return true;

        var raw = NormalizeUrlForCompare(rawUrl);
        var final = NormalizeUrlForCompare(snapshot.FinalUrl);
        if (final is null)
            return false;
        if (raw is not null && string.Equals(raw, final, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(final, UriKind.Absolute, out var finalUri))
            return false;

        return LoadHighValuePathSet().Contains(NormalizeWordlistPath(finalUri.AbsolutePath));
    }

    private static string? NormalizeUrlForCompare(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https"))
            return null;
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/');
    }

    private static string NormalizeWordlistPath(string path)
    {
        var p = path.Trim();
        if (p.Length == 0)
            return "/";
        var q = p.IndexOfAny(['?', '#']);
        if (q >= 0)
            p = p[..q];
        if (!p.StartsWith('/'))
            p = "/" + p;
        return p.TrimEnd('/');
    }

    private static IReadOnlySet<string> LoadHighValuePathSet()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Wordlists", "high_value");
        var list = HighValueWordlistCatalog.LoadFromDirectory(dir);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, lines) in list)
        {
            foreach (var line in lines)
                set.Add(NormalizeWordlistPath(line));
        }

        return set;
    }

    private static string? TryCanonicalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return null;

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var query = NormalizeQuery(uri.Query);
        return $"url:{scheme}://{host}{port}{path}{query}";
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query == "?")
            return "";

        var trimmed = query.TrimStart('?');
        var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(
                p =>
                {
                    var kv = p.Split('=', 2);
                    var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
                    var value = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
                    return new KeyValuePair<string, string>(key, value);
                })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => string.IsNullOrEmpty(p.Value) ? Uri.EscapeDataString(p.Key) : $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")
            .ToArray();

        return parts.Length == 0 ? "" : "?" + string.Join('&', parts);
    }
}
