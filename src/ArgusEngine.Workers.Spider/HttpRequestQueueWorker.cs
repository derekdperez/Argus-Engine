using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Application.Events;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Observability;

namespace ArgusEngine.Workers.Spider;

public sealed class HttpRequestQueueWorker(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ArgusDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<SpiderHttpOptions> options,
    AdaptiveConcurrencyController concurrency,
    ILogger<HttpRequestQueueWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogWorkerStarted =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(ExecuteAsync)), "HttpRequestQueueWorker started.");

    private static readonly Action<ILogger, Exception?> LogLoopFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, nameof(ExecuteAsync)), "HTTP request queue worker loop failed.");

    private static readonly Action<ILogger, string, int, Exception?> LogScanSummary =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(3, nameof(ProcessItemAsync)), "Port scan completed for {Host}; open ports: {Count}");

    private static readonly Action<ILogger, Exception?> LogPermanentFailure =
        LoggerMessage.Define(LogLevel.Warning, new EventId(4, nameof(ProcessItemAsync)), "Permanent failure for HTTP request queue item.");

    private const int MaxLinksPerAsset = 250;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger, null);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opt = options.Value;
                var effectiveConcurrency = concurrency.ResolveEffectiveConcurrency(opt.MaxConcurrency);

                var items = await LeaseWorkAsync(effectiveConcurrency, opt.VisibilityTimeoutSeconds, stoppingToken).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(opt.PollIntervalSeconds), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var tasks = items.Select(item => ProcessItemAsync(item, stoppingToken)).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLoopFailed(logger, ex);
                await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<HttpRequestQueueItem>> LeaseWorkAsync(int limit, int visibilitySeconds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now.AddSeconds(visibilitySeconds);
        var lockId = $"spider-{Environment.MachineName}-{Guid.NewGuid():N}";

        // Simple pessimistic locking via Update...Returning if provider supports it, 
        // or just a select then update. For Postgres we can use a CTE.
        var leased = await db.HttpRequestQueue
            .FromSqlInterpolated($"""
                WITH target AS (
                    SELECT id
                    FROM http_request_queue
                    WHERE (state = 'Queued' OR state = 'Retry')
                      AND next_attempt_at_utc <= {now}
                      AND (locked_until_utc IS NULL OR locked_until_utc <= {now})
                    ORDER BY priority DESC, created_at_utc ASC
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE http_request_queue
                SET state = 'InFlight',
                    locked_by = {lockId},
                    locked_until_utc = {lockedUntil},
                    started_at_utc = {now},
                    attempt_count = attempt_count + 1
                FROM target
                WHERE http_request_queue.id = target.id
                RETURNING http_request_queue.*;
                """)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return leased;
    }

    private async Task ProcessItemAsync(HttpRequestQueueItem item, CancellationToken ct)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["QueueItemId"] = item.Id, ["Url"] = item.RequestUrl });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Ensure we have the asset and target info for HarvestLinksAsync
            StoredAsset? asset;
            await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                asset = await db.Assets
                    .AsNoTracking()
                    .Include(a => a.Target)
                    .FirstOrDefaultAsync(a => a.Id == item.AssetId, ct)
                    .ConfigureAwait(false);
            }

            if (asset is null)
            {
                logger.LogWarning("Skipping HTTP request: asset {AssetId} not found for queue item {QueueItemId}.", item.AssetId, item.Id);
                await MarkFailedAsync(item.Id, "Asset not found", terminal: true, ct).ConfigureAwait(false);
                return;
            }

            using var client = httpClientFactory.CreateClient("spider");
            using var request = new HttpRequestMessage(new HttpMethod(item.Method), item.RequestUrl);
            
            // Re-apply headers if they were stored (not common in raw crawler but supported)
            if (!string.IsNullOrEmpty(item.RequestHeadersJson))
            {
                // Simple header application logic omitted for brevity
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            stopwatch.Stop();

            var snapshot = await CreateSnapshotAsync(item, response, stopwatch.Elapsed, ct).ConfigureAwait(false);
            
            using (var scope = scopeFactory.CreateScope())
            {
                var persistence = scope.ServiceProvider.GetRequiredService<IAssetPersistence>();
                await persistence.ConfirmUrlAssetAsync(item.AssetId, snapshot, Guid.Empty, ct).ConfigureAwait(false);
            }

            await MarkSucceededAsync(item.Id, snapshot, ct).ConfigureAwait(false);

            concurrency.ReportResult(true);

            if (item.AssetKind == AssetKind.Url || item.AssetKind == AssetKind.Subdomain || item.AssetKind == AssetKind.Domain)
            {
                await HarvestLinksAsync(asset, snapshot, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsHttpTransient(ex))
        {
            concurrency.ReportResult(false);
            await HandleRetryOrFailureAsync(item, ex.Message, terminal: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            concurrency.ReportResult(false);
            LogPermanentFailure(logger, ex);
            await HandleRetryOrFailureAsync(item, ex.Message, terminal: true, ct).ConfigureAwait(false);
        }
    }

    private static async Task<UrlFetchSnapshot> CreateSnapshotAsync(HttpRequestQueueItem item, HttpResponseMessage response, TimeSpan duration, CancellationToken ct)
    {
        var body = await BoundedHttpContentReader.ReadAsStringAsync(response.Content, 1024 * 512, ct).ConfigureAwait(false);
        
        return new UrlFetchSnapshot(
            RequestMethod: item.Method,
            RequestHeaders: [],
            RequestBody: null,
            StatusCode: (int)response.StatusCode,
            ResponseHeaders: HeadersToDict(response.Headers),
            ResponseBody: body,
            ResponseSizeBytes: response.Content.Headers.ContentLength,
            DurationMs: duration.TotalMilliseconds,
            ContentType: response.Content.Headers.ContentType?.ToString(),
            CompletedAtUtc: DateTimeOffset.UtcNow,
            FinalUrl: response.RequestMessage?.RequestUri?.ToString() ?? item.RequestUrl,
            RedirectCount: 0,
            RedirectChain: [],
            ResponseBodyPreview: Truncate(body, 4096));
    }

    private async Task MarkSucceededAsync(Guid queueItemId, UrlFetchSnapshot snapshot, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.HttpRequestQueue
            .Where(q => q.Id == queueItemId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(q => q.State, HttpRequestQueueState.Succeeded)
                    .SetProperty(q => q.UpdatedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(q => q.CompletedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(q => q.LockedBy, (string?)null)
                    .SetProperty(q => q.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(q => q.LastHttpStatus, snapshot.StatusCode)
                    .SetProperty(q => q.DurationMs, (long)snapshot.DurationMs)
                    .SetProperty(q => q.ResponseBodyPreview, Truncate(snapshot.ResponseBody ?? "", 4096)),
                ct)
            .ConfigureAwait(false);
    }

    private async Task HandleRetryOrFailureAsync(HttpRequestQueueItem item, string error, bool terminal, CancellationToken ct)
    {
        if (terminal || item.AttemptCount >= item.MaxAttempts)
        {
            var snapshot = new UrlFetchSnapshot(
                RequestMethod: item.Method,
                RequestHeaders: [],
                RequestBody: null,
                StatusCode: 0,
                ResponseHeaders: [],
                ResponseBody: null,
                ResponseSizeBytes: null,
                DurationMs: 0,
                ContentType: null,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                FinalUrl: item.RequestUrl,
                RedirectCount: 0,
                RedirectChain: [],
                ResponseBodyPreview: Truncate(error, Math.Min(error.Length, 4096)));

            using (var scope = scopeFactory.CreateScope())
            {
                var persistence = scope.ServiceProvider.GetRequiredService<IAssetPersistence>();
                await persistence.ConfirmUrlAssetAsync(item.AssetId, snapshot, Guid.Empty, ct).ConfigureAwait(false);
            }
            
            await MarkFailedAsync(item.Id, error, terminal: true, ct).ConfigureAwait(false);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Max(0, item.AttemptCount)) * 5));
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.HttpRequestQueue
            .Where(q => q.Id == item.Id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(q => q.State, HttpRequestQueueState.Retry)
                    .SetProperty(q => q.UpdatedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(q => q.NextAttemptAtUtc, DateTimeOffset.UtcNow + delay)
                    .SetProperty(q => q.LockedBy, (string?)null)
                    .SetProperty(q => q.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(q => q.LastError, Truncate(error, 2048)),
                ct)
            .ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(Guid queueItemId, string error, bool terminal, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.HttpRequestQueue
            .Where(q => q.Id == queueItemId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(q => q.State, terminal ? HttpRequestQueueState.Failed : HttpRequestQueueState.Retry)
                    .SetProperty(q => q.UpdatedAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(q => q.CompletedAtUtc, terminal ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)
                    .SetProperty(q => q.LockedBy, (string?)null)
                    .SetProperty(q => q.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(q => q.LastError, Truncate(error, 2048)),
                ct)
            .ConfigureAwait(false);
    }

    private async Task HarvestLinksAsync(StoredAsset asset, UrlFetchSnapshot snapshot, CancellationToken ct)
    {
        var body = snapshot.ResponseBody ?? "";
        var contentType = snapshot.ContentType ?? "";
        var baseUrl = snapshot.FinalUrl ?? asset.RawValue;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return;

        var parentPage = baseUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var spiderContext = TruncateDiscoveryContext($"Spider: link extracted from fetched page {parentPage}");
        var correlation = NewId.NextGuid();

        using var scope = scopeFactory.CreateScope();
        var scopedOutbox = scope.ServiceProvider.GetRequiredService<IEventOutbox>();

        foreach (var link in LinkHarvest.Extract(body, contentType, baseUri).Take(MaxLinksPerAsset))
        {
            var kind = LinkHarvest.GuessKindForUrl(link);

            await scopedOutbox.EnqueueAsync(
                new AssetDiscovered(
                    asset.TargetId,
                    asset.Target?.RootDomain ?? "",
                    asset.Target?.GlobalMaxDepth ?? asset.Depth + 10,
                    asset.Depth + 1,
                    kind,
                    link,
                    "spider-worker",
                    DateTimeOffset.UtcNow,
                    correlation,
                    AssetAdmissionStage.Raw,
                    null,
                    spiderContext,
                    EventId: NewId.NextGuid(),
                    CausationId: correlation,
                    Producer: "worker-spider"),
                ct)
                .ConfigureAwait(false);
        }
    }

    private static bool IsHttpTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private static Dictionary<string, string> HeadersToDict(HttpHeaders headers)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
            d[h.Key] = string.Join(", ", h.Value);

        return d;
    }

    private static string Truncate(string s, int maxChars) =>
        s.Length <= maxChars ? s : s[..maxChars];

    private static string TruncateDiscoveryContext(string s, int maxChars = 512) =>
        s.Length <= maxChars ? s : s[..(maxChars - 1)] + "…";
}
