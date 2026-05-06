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
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(ExecuteAsync)),
            "HttpRequestQueueWorker started.");

    private static readonly Action<ILogger, Exception?> LogLoopFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(ExecuteAsync)),
            "HTTP request queue worker loop failed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> LogAssetNotFound =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(5, nameof(ProcessItemAsync)),
            "Skipping HTTP request: asset {AssetId} not found for queue item {QueueItemId}.");

    private static readonly Action<ILogger, Exception?> LogPermanentFailure =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4, nameof(ProcessItemAsync)),
            "Permanent failure for HTTP request queue item.");

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

                var tasks = new Task[items.Count];
                for (var i = 0; i < items.Count; i++)
                {
                    tasks[i] = ProcessItemAsync(items[i], stoppingToken);
                }

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

        var leased = await db.HttpRequestQueue
            .FromSqlInterpolated($"""
                WITH target AS (
                    SELECT id
                    FROM http_request_queue
                    WHERE (state = 'Queued' OR state = 'Retry')
                      AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
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
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["QueueItemId"] = item.Id,
            ["Url"] = item.RequestUrl,
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
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
                LogAssetNotFound(logger, item.AssetId, item.Id, null);
                await MarkFailedAsync(item.Id, "Asset not found", terminal: true, ct).ConfigureAwait(false);
                return;
            }

            using var client = httpClientFactory.CreateClient("spider");
            using var request = new HttpRequestMessage(new HttpMethod(item.Method), item.RequestUrl);

            if (!string.IsNullOrEmpty(item.RequestHeadersJson))
            {
                // Simple header application logic omitted for brevity in current implementation.
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
                    .SetProperty(q => q.ResponseBodyPreview, Truncate(snapshot.ResponseBody ?? string.Empty, 4096)),
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
        var body = snapshot.ResponseBody ?? string.Empty;
        var contentType = snapshot.ContentType ?? string.Empty;
        var baseUrl = snapshot.FinalUrl ?? asset.RawValue;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return;

        var links = LinkHarvest.Extract(body, contentType, baseUri, MaxLinksPerAsset);
        if (links.Count == 0)
            return;

        var parentPage = baseUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var spiderContext = TruncateDiscoveryContext($"Spider: link extracted from fetched page {parentPage}");
        var correlation = NewId.NextGuid();
        var now = DateTimeOffset.UtcNow;
        var targetRootDomain = asset.Target?.RootDomain ?? string.Empty;
        var globalMaxDepth = asset.Target?.GlobalMaxDepth ?? asset.Depth + 10;
        var nextDepth = asset.Depth + 1;
        var events = new List<AssetDiscovered>(links.Count);

        foreach (var link in links)
        {
            events.Add(
                new AssetDiscovered(
                    asset.TargetId,
                    targetRootDomain,
                    globalMaxDepth,
                    nextDepth,
                    LinkHarvest.GuessKindForUrl(link),
                    link,
                    "spider-worker",
                    now,
                    correlation,
                    AssetAdmissionStage.Raw,
                    null,
                    spiderContext,
                    EventId: NewId.NextGuid(),
                    CausationId: correlation,
                    Producer: "worker-spider"));
        }

        using var scope = scopeFactory.CreateScope();
        var scopedOutbox = scope.ServiceProvider.GetRequiredService<IEventOutbox>();
        await scopedOutbox.EnqueueBatchAsync(events, ct).ConfigureAwait(false);
    }

    private static bool IsHttpTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private static Dictionary<string, string> HeadersToDict(HttpHeaders headers)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            dictionary[header.Key] = string.Join(", ", header.Value);
        }

        return dictionary;
    }

    private static string Truncate(string s, int maxChars) =>
        s.Length <= maxChars ? s : s[..maxChars];

    private static string TruncateDiscoveryContext(string s, int maxChars = 512) =>
        s.Length <= maxChars ? s : s[..(maxChars - 1)] + "…";
}
