using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Http;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Observability;

namespace ArgusEngine.Workers.HttpRequester;

public sealed class HttpRequesterWorker(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ArgusDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<HttpRequesterOptions> options,
    AdaptiveConcurrencyController concurrency,
    IHttpRateLimiter rateLimiter,
    IPublishEndpoint publishEndpoint,
    ILogger<HttpRequesterWorker> logger) : BackgroundService
{
    private HttpRequestQueueSettings? _currentSettings;
    private DateTimeOffset _lastSettingsFetch = DateTimeOffset.MinValue;
    private static readonly string[] DefaultUserAgents = [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:125.0) Gecko/20100101 Firefox/125.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4.1 Mobile/15E148 Safari/604.1"
    ];

    private static readonly Action<ILogger, Exception?> LogWorkerStarted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(ExecuteAsync)),
            "HttpRequesterWorker started.");

    private static readonly Action<ILogger, Exception?> LogLoopFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(ExecuteAsync)),
            "HTTP requester worker loop failed.");

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
        var lockId = $"requester-{Environment.MachineName}-{Guid.NewGuid():N}";

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
            ["DomainKey"] = item.DomainKey,
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var settings = await GetSettingsAsync(ct).ConfigureAwait(false);

            // Apply per-domain rate limiting
            await rateLimiter.WaitAsync(item.DomainKey, ct).ConfigureAwait(false);

            // Apply Jitter
            if (settings?.UseRandomJitter == true && settings.MaxJitterMs > settings.MinJitterMs)
            {
                var jitter = Random.Shared.Next(settings.MinJitterMs, settings.MaxJitterMs);
                await Task.Delay(jitter, ct).ConfigureAwait(false);
            }

            using var client = httpClientFactory.CreateClient("requester");
            using var request = new HttpRequestMessage(new HttpMethod(item.Method), item.RequestUrl);

            // Apply Detection Evasion
            ApplyEvasion(request, settings);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            stopwatch.Stop();

            var snapshot = await CreateSnapshotAsync(item, response, stopwatch.Elapsed, ct).ConfigureAwait(false);

            StoredAsset? asset = null;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
                asset = await db.Assets.Include(a => a.Target).FirstOrDefaultAsync(a => a.Id == item.AssetId, ct).ConfigureAwait(false);
                
                if (asset == null)
                {
                    LogAssetNotFound(logger, item.AssetId, item.Id, null);
                    await MarkFailedAsync(item.Id, "Asset not found", terminal: true, ct).ConfigureAwait(false);
                    return;
                }

                var persistence = scope.ServiceProvider.GetRequiredService<IAssetPersistence>();
                await persistence.ConfirmUrlAssetAsync(item.AssetId, snapshot, Guid.Empty, ct).ConfigureAwait(false);
            }

            await MarkSucceededAsync(item.Id, snapshot, ct).ConfigureAwait(false);
            
            // Record rate limit success
            rateLimiter.RecordCompletion(item.DomainKey, true, stopwatch.Elapsed);
            concurrency.ReportResult(true);

            // Publish event for spidering and other processing
            await publishEndpoint.Publish(new HttpResponseDownloaded(
                item.TargetId,
                asset.Target?.RootDomain ?? string.Empty,
                asset.Target?.GlobalMaxDepth ?? asset.Depth + 10,
                item.AssetId,
                asset.Depth,
                item.AssetKind,
                snapshot,
                DateTimeOffset.UtcNow,
                NewId.NextGuid(),
                Producer: "worker-http-requester"), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsNameResolutionFailure(ex))
        {
            concurrency.ReportResult(false);
            rateLimiter.RecordCompletion(item.DomainKey, false, stopwatch.Elapsed);
            await HandleRetryOrFailureAsync(item, DnsFailureMessage(item.RequestUrl), terminal: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHttpTransient(ex))
        {
            concurrency.ReportResult(false);
            rateLimiter.RecordCompletion(item.DomainKey, false, stopwatch.Elapsed);
            await HandleRetryOrFailureAsync(item, ex.Message, terminal: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            concurrency.ReportResult(false);
            rateLimiter.RecordCompletion(item.DomainKey, false, stopwatch.Elapsed);
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

    private async Task<HttpRequestQueueSettings?> GetSettingsAsync(CancellationToken ct)
    {
        if (_currentSettings != null && DateTimeOffset.UtcNow - _lastSettingsFetch < TimeSpan.FromMinutes(1))
        {
            return _currentSettings;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
            _currentSettings = await db.HttpRequestQueueSettings.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
            _lastSettingsFetch = DateTimeOffset.UtcNow;
            return _currentSettings;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh HTTP request queue settings. Using cached or default.");
            return _currentSettings;
        }
    }

    private void ApplyEvasion(HttpRequestMessage request, HttpRequestQueueSettings? settings)
    {
        if (settings == null) return;

        // User Agent Rotation
        if (settings.RotateUserAgents)
        {
            string ua = DefaultUserAgents[Random.Shared.Next(DefaultUserAgents.Length)];
            
            if (!string.IsNullOrWhiteSpace(settings.CustomUserAgentsJson))
            {
                try
                {
                    var customUas = System.Text.Json.JsonSerializer.Deserialize<string[]>(settings.CustomUserAgentsJson);
                    if (customUas != null && customUas.Length > 0)
                    {
                        ua = customUas[Random.Shared.Next(customUas.Length)];
                    }
                }
                catch { /* Ignore invalid JSON */ }
            }

            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", ua);
        }

        // Spoof Referer
        if (settings.SpoofReferer)
        {
            string[] referers = [
                "https://www.google.com/",
                "https://www.bing.com/",
                "https://duckduckgo.com/",
                new Uri(request.RequestUri!).GetLeftPart(UriPartial.Authority) + "/"
            ];
            request.Headers.Referrer = new Uri(referers[Random.Shared.Next(referers.Length)]);
        }

        // Custom Headers
        if (!string.IsNullOrWhiteSpace(settings.CustomHeadersJson))
        {
            try
            {
                var customHeaders = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(settings.CustomHeadersJson);
                if (customHeaders != null)
                {
                    foreach (var kvp in customHeaders)
                    {
                        request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                    }
                }
            }
            catch { /* Ignore invalid JSON */ }
        }
    }

    private static Dictionary<string, string> HeadersToDict(HttpResponseHeaders headers) =>
        headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

    private static string Truncate(string? value, int max) =>
        value?.Length > max ? value[..max] : (value ?? string.Empty);

    private static bool IsHttpTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private static bool IsNameResolutionFailure(HttpRequestException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData };

    private static string DnsFailureMessage(string url) => $"DNS resolution failed for {url}";
}
