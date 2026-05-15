using System.Net.Http.Headers;
using System.Net.Sockets;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Application.Http;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Assets;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Observability;
using ArgusEngine.Workers.Spider;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Workers.HttpRequester;

public sealed class HttpRequesterWorker(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ArgusDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<HttpRequesterOptions> options,
    AdaptiveConcurrencyController concurrency,
    IHttpRateLimiter rateLimiter,
    ProxyHttpClientProvider proxyHttpClientProvider,
    IPublishEndpoint publishEndpoint,
    ILogger<HttpRequesterWorker> logger) : BackgroundService
{
    private HttpRequestQueueSettings? _currentSettings;
    private DateTimeOffset _lastSettingsFetch = DateTimeOffset.MinValue;

    private static readonly Action<ILogger, Exception?> LogWorkerStarted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1, nameof(ExecuteAsync)),
        "HttpRequesterWorker started.");

    private static readonly Action<ILogger, Exception?> LogLoopFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2, nameof(ExecuteAsync)),
        "HTTP requester worker loop failed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> LogAssetNotFound = LoggerMessage.Define<Guid, Guid>(
        LogLevel.Warning,
        new EventId(5, nameof(ProcessItemAsync)),
        "Skipping HTTP request: asset {AssetId} not found for queue item {QueueItemId}.");

    private static readonly Action<ILogger, Exception?> LogPermanentFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4, nameof(ProcessItemAsync)),
        "Permanent failure for HTTP request queue item.");

    private static readonly Action<ILogger, string, string, Exception?> LogProxySelected =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(6, nameof(ProcessItemAsync)),
            "Routing HTTP request through proxy {ProxyId} for domain {DomainKey}.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger, null);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opt = options.Value;
                var effectiveConcurrency = concurrency.ResolveEffectiveConcurrency(opt.MaxConcurrency);

                var items = await LeaseWorkAsync(effectiveConcurrency, opt.VisibilityTimeoutSeconds, stoppingToken)
                    .ConfigureAwait(false);

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

    private async Task<IReadOnlyList<HttpRequestQueueItem>> LeaseWorkAsync(
        int limit,
        int visibilitySeconds,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

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
                      AND NOT EXISTS (
                          SELECT 1
                          FROM http_request_queue AS in_flight
                          WHERE in_flight.domain_key = http_request_queue.domain_key
                            AND in_flight.state = 'InFlight'
                            AND in_flight.locked_until_utc IS NOT NULL
                            AND in_flight.locked_until_utc > {now}
                      )
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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return leased;
    }

    private async Task ProcessItemAsync(HttpRequestQueueItem item, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["QueueItemId"] = item.Id,
            ["Url"] = item.RequestUrl,
            ["DomainKey"] = item.DomainKey
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false) ?? new HttpRequestQueueSettings();

            await rateLimiter.WaitAsync(item.DomainKey, cancellationToken).ConfigureAwait(false);

            var opt = options.Value;
            var proxy = ProxyRouting.SelectProxy(settings, item);
            ProxyTargetFingerprintProfile? proxyFingerprintProfile = null;
            if (proxy is not null && settings.ProxyFingerprintingEnabled)
            {
                proxyFingerprintProfile = await GetOrCreateProxyFingerprintProfileAsync(settings, item, proxy, cancellationToken)
                    .ConfigureAwait(false);
            }

            var client = proxy is null
                ? httpClientFactory.CreateClient("requester")
                : proxyHttpClientProvider.GetClient(proxy, opt);

            if (proxy is not null)
            {
                LogProxySelected(logger, string.IsNullOrWhiteSpace(proxy.Id) ? proxy.CacheKey : proxy.Id, item.DomainKey, null);
            }

            var delayMs = ProxyFingerprinting.GetDelayMs(settings, proxyFingerprintProfile);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(new HttpMethod(item.Method), item.RequestUrl);
            ApplyRequestHeaders(request, opt, settings, proxyFingerprintProfile);
            if (proxyFingerprintProfile is not null)
            {
                await RecordProxyFingerprintUseAsync(proxyFingerprintProfile.Id, item.RequestUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            var snapshot = await CreateSnapshotAsync(item, request, response, stopwatch.Elapsed, cancellationToken)
                .ConfigureAwait(false);

            StoredAsset? asset;
            var correlationId = NewId.NextGuid();

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
                asset = await db.Assets
                    .Include(a => a.Target)
                    .FirstOrDefaultAsync(a => a.Id == item.AssetId, cancellationToken)
                    .ConfigureAwait(false);

                if (asset is null)
                {
                    LogAssetNotFound(logger, item.AssetId, item.Id, null);
                    await MarkFailedAsync(item.Id, "Asset not found", terminal: true, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var persistence = scope.ServiceProvider.GetRequiredService<IAssetPersistence>();
                await persistence.ConfirmUrlAssetAsync(item.AssetId, snapshot, correlationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await MarkSucceededAsync(item.Id, snapshot, cancellationToken).ConfigureAwait(false);

            rateLimiter.RecordCompletion(item.DomainKey, true, stopwatch.Elapsed);
            concurrency.ReportResult(true);

            await publishEndpoint.Publish(
                    new HttpResponseDownloaded(
                        item.TargetId,
                        asset.Target?.RootDomain ?? string.Empty,
                        asset.Target?.GlobalMaxDepth ?? asset.Depth + 10,
                        item.AssetId,
                        asset.Depth,
                        item.AssetKind,
                        snapshot,
                        DateTimeOffset.UtcNow,
                        correlationId,
                        EventId: NewId.NextGuid(),
                        CausationId: item.Id,
                        Producer: "worker-http-requester"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsNameResolutionFailure(ex))
        {
            concurrency.ReportResult(false);
            rateLimiter.RecordCompletion(item.DomainKey, false, stopwatch.Elapsed);
            await HandleRetryOrFailureAsync(item, DnsFailureMessage(item.RequestUrl), terminal: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHttpTransient(ex))
        {
            concurrency.ReportResult(false);
            rateLimiter.RecordCompletion(item.DomainKey, false, stopwatch.Elapsed);
            await HandleRetryOrFailureAsync(item, ex.Message, terminal: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            concurrency.ReportResult(false);
            rateLimiter.RecordCompletion(item.DomainKey, false, stopwatch.Elapsed);
            LogPermanentFailure(logger, ex);
            await HandleRetryOrFailureAsync(item, ex.Message, terminal: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        HttpRequesterOptions options,
        HttpRequestQueueSettings settings,
        ProxyTargetFingerprintProfile? proxyFingerprintProfile)
    {
        IReadOnlyList<KeyValuePair<string, string>> headers;
        if (proxyFingerprintProfile is not null)
        {
            headers = ProxyFingerprinting.BuildHeaders(proxyFingerprintProfile, settings);
        }
        else
        {
            headers =
            [
                new KeyValuePair<string, string>("User-Agent", string.IsNullOrWhiteSpace(options.UserAgent) ? "ArgusEngine.HttpRequester/1.0" : options.UserAgent),
                new KeyValuePair<string, string>("Accept", "*/*")
            ];
        }

        request.Headers.Accept.Clear();
        request.Headers.UserAgent.Clear();

        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (request.Headers.Accept.Count == 0)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        }
    }

    private static async Task<UrlFetchSnapshot> CreateSnapshotAsync(
        HttpRequestQueueItem item,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var body = await BoundedHttpContentReader
            .ReadAsStringAsync(response.Content, 1024 * 512, cancellationToken)
            .ConfigureAwait(false);

        var responseHeaders = HeadersToDict(response.Headers);
        foreach (var header in response.Content.Headers)
        {
            responseHeaders[header.Key] = string.Join(", ", header.Value);
        }

        return new UrlFetchSnapshot(
            RequestMethod: item.Method,
            RequestHeaders: HeadersToDict(request.Headers),
            RequestBody: null,
            StatusCode: (int)response.StatusCode,
            ResponseHeaders: responseHeaders,
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

    private async Task MarkSucceededAsync(Guid queueItemId, UrlFetchSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

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
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleRetryOrFailureAsync(
        HttpRequestQueueItem item,
        string error,
        bool terminal,
        CancellationToken cancellationToken)
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
                await persistence.ConfirmUrlAssetAsync(item.AssetId, snapshot, NewId.NextGuid(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await MarkFailedAsync(item.Id, error, terminal: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Max(0, item.AttemptCount)) * 5));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

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
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(Guid queueItemId, string error, bool terminal, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

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
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProxyTargetFingerprintProfile> GetOrCreateProxyFingerprintProfileAsync(
        HttpRequestQueueSettings settings,
        HttpRequestQueueItem item,
        ProxyServerConfiguration proxy,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);

        var proxyId = string.IsNullOrWhiteSpace(proxy.Id) ? proxy.CacheKey : proxy.Id;
        var targetKey = ProxyRouting.NormalizeAssignmentKey(item.DomainKey, item.RequestUrl);

        var existing = await db.ProxyTargetFingerprintProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.ProxyId == proxyId && row.TargetKey == targetKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var created = ProxyFingerprinting.CreateProfile(settings, item, proxy);
        created.ProxyId = proxyId;
        created.TargetKey = targetKey;

        db.ProxyTargetFingerprintProfiles.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (DbUpdateException)
        {
            // Concurrent workers may race to create the same profile. Read the winner.
            var winner = await db.ProxyTargetFingerprintProfiles
                .AsNoTracking()
                .FirstAsync(
                    row => row.ProxyId == proxyId && row.TargetKey == targetKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return winner;
        }
    }

    private async Task RecordProxyFingerprintUseAsync(Guid profileId, string requestUrl, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await db.ProxyTargetFingerprintProfiles
            .Where(row => row.Id == profileId)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.RequestCount, row => row.RequestCount + 1)
                    .SetProperty(row => row.UpdatedAtUtc, now)
                    .SetProperty(row => row.LastUsedAtUtc, now)
                    .SetProperty(row => row.LastRequestUrl, requestUrl),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpRequestQueueSettings?> GetSettingsAsync(CancellationToken cancellationToken)
    {
        if (_currentSettings is not null && DateTimeOffset.UtcNow - _lastSettingsFetch < TimeSpan.FromMinutes(1))
        {
            return _currentSettings;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

            await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);

            _currentSettings = await db.HttpRequestQueueSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            _lastSettingsFetch = DateTimeOffset.UtcNow;

            return _currentSettings;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh HTTP request queue settings. Using cached or default settings.");
            return _currentSettings;
        }
    }

    private static async Task EnsureProxyColumnsAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE http_request_queue_settings
                ADD COLUMN IF NOT EXISTS proxy_routing_enabled boolean NOT NULL DEFAULT false,
                ADD COLUMN IF NOT EXISTS proxy_sticky_subdomains_enabled boolean NOT NULL DEFAULT true,
                ADD COLUMN IF NOT EXISTS proxy_assignment_salt text NULL DEFAULT 'argus-proxy-v1',
                ADD COLUMN IF NOT EXISTS proxy_servers_json text NULL DEFAULT '[]',
                ADD COLUMN IF NOT EXISTS proxy_fingerprinting_enabled boolean NOT NULL DEFAULT true,
                ADD COLUMN IF NOT EXISTS proxy_fingerprint_min_delay_ms integer NOT NULL DEFAULT 150,
                ADD COLUMN IF NOT EXISTS proxy_fingerprint_max_delay_ms integer NOT NULL DEFAULT 1400;

            CREATE TABLE IF NOT EXISTS proxy_target_fingerprint_profiles (
                id uuid NOT NULL PRIMARY KEY,
                proxy_id character varying(128) NOT NULL,
                proxy_name character varying(256) NOT NULL,
                proxy_public_ip character varying(64) NULL,
                target_key character varying(253) NOT NULL,
                browser_family character varying(64) NOT NULL,
                browser_version character varying(64) NOT NULL,
                platform character varying(64) NOT NULL,
                accept_language character varying(128) NOT NULL,
                viewport_width integer NOT NULL,
                viewport_height integer NOT NULL,
                user_agent character varying(512) NOT NULL,
                referer_template character varying(256) NOT NULL,
                header_profile_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                delay_min_ms integer NOT NULL DEFAULT 150,
                delay_max_ms integer NOT NULL DEFAULT 1400,
                request_count bigint NOT NULL DEFAULT 0,
                created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                last_used_at_utc timestamp with time zone NULL,
                last_request_url character varying(4096) NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_proxy_target_fingerprint_profiles_proxy_target
                ON proxy_target_fingerprint_profiles (proxy_id, target_key);
            CREATE INDEX IF NOT EXISTS ix_proxy_target_fingerprint_profiles_last_used
                ON proxy_target_fingerprint_profiles (last_used_at_utc);
            """,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private static Dictionary<string, string> HeadersToDict(HttpHeaders headers) =>
        headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);

    private static string Truncate(string? value, int max) => value?.Length > max ? value[..max] : value ?? string.Empty;

    private static bool IsHttpTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private static bool IsNameResolutionFailure(HttpRequestException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData };

    private static string DnsFailureMessage(string url) => $"DNS resolution failed for {url}";
}
