using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.FileStore;
using NightmareV2.Application.Gatekeeping;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Observability;

namespace NightmareV2.Workers.Spider;

public sealed class HttpRequestQueueWorker(
    IDbContextFactory<NightmareDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IServiceScopeFactory scopeFactory,
    IHttpRequestQueueStateMachine stateMachine,
    IHttpArtifactStore artifactStore,
    ILogger<HttpRequestQueueWorker> logger) : BackgroundService
{
    private const int MaxLinksPerAsset = 500;
    private const int MaxBodyCaptureChars = 200_000;
    private const int MaxRedirects = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly AdaptiveConcurrencyController _adaptiveConcurrency = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HTTP request queue worker {WorkerId} starting.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await IsSpiderEnabledAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ReapExpiredLocksAsync(stoppingToken).ConfigureAwait(false);

                var lease = await TryLeaseNextAsync(stoppingToken).ConfigureAwait(false);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "HTTP request queue worker loop fault.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> IsSpiderEnabledAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var toggles = scope.ServiceProvider.GetRequiredService<IWorkerToggleReader>();
        return await toggles.IsWorkerEnabledAsync(WorkerKeys.Spider, ct).ConfigureAwait(false);
    }

    private async Task ReapExpiredLocksAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await db.HttpRequestQueue
            .Where(q => q.State == HttpRequestQueueState.InFlight && q.LockedUntilUtc < now && q.AttemptCount < q.MaxAttempts)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(q => q.State, HttpRequestQueueState.Retry)
                    .SetProperty(q => q.UpdatedAtUtc, now)
                    .SetProperty(q => q.NextAttemptAtUtc, now)
                    .SetProperty(q => q.LockedBy, (string?)null)
                    .SetProperty(q => q.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(q => q.LastError, "Worker lock expired before completion; request will be retried."),
                ct)
            .ConfigureAwait(false);

        await db.HttpRequestQueue
            .Where(q => q.State == HttpRequestQueueState.InFlight && q.LockedUntilUtc < now && q.AttemptCount >= q.MaxAttempts)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(q => q.State, HttpRequestQueueState.Failed)
                    .SetProperty(q => q.UpdatedAtUtc, now)
                    .SetProperty(q => q.CompletedAtUtc, now)
                    .SetProperty(q => q.LockedBy, (string?)null)
                    .SetProperty(q => q.LockedUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(q => q.LastError, "Worker lock expired after the final allowed attempt."),
                ct)
            .ConfigureAwait(false);
    }

    private async Task<HttpRequestQueueItem?> TryLeaseNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var oneMinuteAgo = now.AddMinutes(-1);
        var lockUntil = now.AddMinutes(5);

        var settings = await db.HttpRequestQueueSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct)
            .ConfigureAwait(false) ?? new HttpRequestQueueSettings();

        if (!settings.Enabled)
            return null;

        var effectiveMaxConcurrency = _adaptiveConcurrency.ResolveEffectiveConcurrency(settings.MaxConcurrency);

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            WITH candidate AS (
                SELECT q.*
                FROM http_request_queue q
                JOIN recon_targets t ON t."Id" = q.target_id
                WHERE @settings_enabled = true
                  AND (
                        (q.state IN ('Queued', 'Retry') AND q.next_attempt_at_utc <= @now)
                     OR (q.state = 'InFlight' AND q.locked_until_utc < @now)
                  )
                  AND q.attempt_count < q.max_attempts
                  AND (
                        SELECT COUNT(*)
                        FROM http_request_queue running
                        WHERE running.state = 'InFlight'
                          AND running.locked_until_utc > @now
                  ) < LEAST(@max_concurrency, @effective_max_concurrency)
                  AND (
                        SELECT COUNT(*)
                        FROM http_request_queue recent_global
                        WHERE recent_global.started_at_utc IS NOT NULL
                          AND recent_global.started_at_utc >= @one_minute_ago
                  ) < @global_requests_per_minute
                  AND (
                        SELECT COUNT(*)
                        FROM http_request_queue recent_domain
                        WHERE recent_domain.domain_key = q.domain_key
                          AND recent_domain.started_at_utc IS NOT NULL
                          AND recent_domain.started_at_utc >= @one_minute_ago
                  ) < @per_domain_requests_per_minute
                ORDER BY
                    q.priority DESC,
                    CASE
                        WHEN lower(q.domain_key) = lower(t."RootDomain") THEN 0
                        WHEN lower(q.domain_key) LIKE '%.' || lower(t."RootDomain") THEN 1
                        ELSE 2
                    END ASC,
                    CASE
                        WHEN lower(q.domain_key) = lower(t."RootDomain") THEN 0
                        WHEN lower(q.domain_key) LIKE '%.' || lower(t."RootDomain") THEN array_length(
                            string_to_array(
                                left(lower(q.domain_key), GREATEST(length(q.domain_key) - length(t."RootDomain") - 1, 0)),
                                '.'),
                            1)
                        ELSE array_length(string_to_array(lower(q.domain_key), '.'), 1)
                    END ASC,
                    CASE
                        WHEN lower(q.domain_key) = lower(t."RootDomain") THEN 0
                        WHEN lower(q.domain_key) LIKE '%.' || lower(t."RootDomain") THEN length(left(lower(q.domain_key), GREATEST(length(q.domain_key) - length(t."RootDomain") - 1, 0)))
                        ELSE length(q.domain_key)
                    END ASC,
                    q.next_attempt_at_utc ASC,
                    q.created_at_utc ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE http_request_queue q
            SET state = 'InFlight',
                locked_by = @worker_id,
                locked_until_utc = @lock_until,
                started_at_utc = @now,
                updated_at_utc = @now,
                attempt_count = q.attempt_count + 1,
                last_error = NULL
            FROM candidate
            WHERE q.id = candidate.id
            RETURNING q.*;
            """;

        AddParameter(cmd, "now", now);
        AddParameter(cmd, "one_minute_ago", oneMinuteAgo);
        AddParameter(cmd, "worker_id", _workerId);
        AddParameter(cmd, "lock_until", lockUntil);
        AddParameter(cmd, "effective_max_concurrency", effectiveMaxConcurrency);
        AddParameter(cmd, "settings_enabled", settings.Enabled);
        AddParameter(cmd, "max_concurrency", Math.Clamp(settings.MaxConcurrency, 1, 512));
        AddParameter(cmd, "global_requests_per_minute", Math.Clamp(settings.GlobalRequestsPerMinute, 1, 60_000));
        AddParameter(cmd, "per_domain_requests_per_minute", Math.Clamp(settings.PerDomainRequestsPerMinute, 1, 60_000));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return MapQueueItem(reader);
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static HttpRequestQueueItem MapQueueItem(DbDataReader reader) => new()
    {
        Id = reader.GetGuid(reader.GetOrdinal("id")),
        AssetId = reader.GetGuid(reader.GetOrdinal("asset_id")),
        TargetId = reader.GetGuid(reader.GetOrdinal("target_id")),
        AssetKind = (AssetKind)reader.GetInt32(reader.GetOrdinal("asset_kind")),
        Method = reader.GetString(reader.GetOrdinal("method")),
        RequestUrl = reader.GetString(reader.GetOrdinal("request_url")),
        DomainKey = reader.GetString(reader.GetOrdinal("domain_key")),
        State = reader.GetString(reader.GetOrdinal("state")),
        Priority = reader.GetInt32(reader.GetOrdinal("priority")),
        AttemptCount = reader.GetInt32(reader.GetOrdinal("attempt_count")),
        MaxAttempts = reader.GetInt32(reader.GetOrdinal("max_attempts")),
        CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at_utc")),
        UpdatedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at_utc")),
        NextAttemptAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("next_attempt_at_utc")),
        LockedBy = ReadNullableString(reader, "locked_by"),
        LockedUntilUtc = ReadNullableDateTimeOffset(reader, "locked_until_utc"),
        StartedAtUtc = ReadNullableDateTimeOffset(reader, "started_at_utc"),
        CompletedAtUtc = ReadNullableDateTimeOffset(reader, "completed_at_utc"),
        DurationMs = ReadNullableInt64(reader, "duration_ms"),
        LastHttpStatus = ReadNullableInt32(reader, "last_http_status"),
        LastError = ReadNullableString(reader, "last_error"),
        RequestHeadersJson = ReadNullableString(reader, "request_headers_json"),
        RequestBody = ReadNullableString(reader, "request_body"),
        ResponseHeadersJson = ReadNullableString(reader, "response_headers_json"),
        ResponseBody = ReadNullableString(reader, "response_body"),
        ResponseContentType = ReadNullableString(reader, "response_content_type"),
        ResponseContentLength = ReadNullableInt64(reader, "response_content_length"),
        FinalUrl = ReadNullableString(reader, "final_url"),
        RedirectCount = ReadNullableInt32(reader, "redirect_count") ?? 0,
        RedirectChainJson = ReadNullableString(reader, "redirect_chain_json"),
        RequestHeadersBlobId = ReadNullableGuid(reader, "request_headers_blob_id"),
        RequestBodyBlobId = ReadNullableGuid(reader, "request_body_blob_id"),
        ResponseHeadersBlobId = ReadNullableGuid(reader, "response_headers_blob_id"),
        ResponseBodyBlobId = ReadNullableGuid(reader, "response_body_blob_id"),
        RedirectChainBlobId = ReadNullableGuid(reader, "redirect_chain_blob_id"),
        ResponseBodySha256 = ReadNullableString(reader, "response_body_sha256"),
        ResponseBodyPreview = ReadNullableString(reader, "response_body_preview"),
        ResponseBodyTruncated = ReadBoolean(reader, "response_body_truncated")
    };

    private static string? ReadNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? ReadNullableGuid(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static int? ReadNullableInt32(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? ReadNullableInt64(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private static bool ReadBoolean(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }

    private async Task ProcessLeaseAsync(HttpRequestQueueItem item, CancellationToken ct)
    {
        using var activity = ArgusTracing.Source.StartActivity("http_queue.process_item");
        activity?.SetTag("argus.queue_item_id", item.Id);
        activity?.SetTag("argus.asset_id", item.AssetId);
        activity?.SetTag("argus.target_id", item.TargetId);
        activity?.SetTag("argus.worker", WorkerKeys.Spider);
        activity?.SetTag("http.url", item.RequestUrl);

        var started = Stopwatch.GetTimestamp();

        ArgusMeters.ActiveWorkerLeases.Add(
            1,
            new KeyValuePair<string, object?>("worker", WorkerKeys.Spider));

        try
        {
            await ProcessLeaseCoreAsync(item, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            ArgusMeters.ActiveWorkerLeases.Add(
                -1,
                new KeyValuePair<string, object?>("worker", WorkerKeys.Spider));

            ArgusMeters.WorkerLoopDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("worker", WorkerKeys.Spider));
        }
    }

    private async Task ProcessLeaseCoreAsync(HttpRequestQueueItem item, CancellationToken ct)
    {
        HttpRequestQueueSettings settings;
        StoredAsset? asset;

        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            settings = await db.HttpRequestQueueSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct)
                .ConfigureAwait(false) ?? new HttpRequestQueueSettings();

            asset = await db.Assets.AsNoTracking()
                .Include(a => a.Target)
                .FirstOrDefaultAsync(a => a.Id == item.AssetId, ct)
                .ConfigureAwait(false);
        }

        if (asset is null)
        {
            await MarkFailedAsync(item.Id, "Asset no longer exists.", terminal: true, ct).ConfigureAwait(false);
            return;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 300));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var snapshot = await SendAsync(item, timeoutCts.Token).ConfigureAwait(false);

            ArgusMeters.HttpRequestsCompleted.Add(
                1,
                new KeyValuePair<string, object?>("status_code", snapshot.StatusCode),
                new KeyValuePair<string, object?>("worker", WorkerKeys.Spider));

            ArgusMeters.HttpFetchDurationMs.Record(
                snapshot.DurationMs,
                new KeyValuePair<string, object?>("status_code", snapshot.StatusCode),
                new KeyValuePair<string, object?>("worker", WorkerKeys.Spider));

            Activity.Current?.SetTag("http.status_code", snapshot.StatusCode);
            Activity.Current?.SetTag("argus.final_url", snapshot.FinalUrl);
            Activity.Current?.SetTag("argus.redirect_count", snapshot.RedirectCount);

            if (ShouldQueueRetry((HttpStatusCode)snapshot.StatusCode) && item.AttemptCount < item.MaxAttempts)
            {
                await SaveRetryResponseAsync(item, snapshot, $"Transient HTTP {snapshot.StatusCode}; retry scheduled.", ct)
                    .ConfigureAwait(false);
                return;
            }

            var terminalState = ShouldQueueRetry((HttpStatusCode)snapshot.StatusCode)
                ? HttpRequestQueueState.Failed
                : HttpRequestQueueState.Succeeded;

            var persistedSnapshot = await SaveResponseAsync(item.Id, snapshot, terminalState, null, terminal: true, ct)
                .ConfigureAwait(false);

            _adaptiveConcurrency.ReportResult(terminalState == HttpRequestQueueState.Succeeded);

            using (var scope = scopeFactory.CreateScope())
            {
                var persistence = scope.ServiceProvider.GetRequiredService<IAssetPersistence>();
                await persistence.ConfirmUrlAssetAsync(item.AssetId, persistedSnapshot, Guid.Empty, ct).ConfigureAwait(false);
            }

            if (snapshot.StatusCode is >= 200 and < 300 && !UrlFetchClassifier.LooksLikeSoft404(snapshot))
                await HarvestLinksAsync(asset, snapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RetryOrFailAsync(item, "HTTP request timed out.", ct).ConfigureAwait(false);
            _adaptiveConcurrency.ReportResult(false);
        }
        catch (Exception ex) when (IsHttpTransient(ex))
        {
            await RetryOrFailAsync(item, ex.Message, ct).ConfigureAwait(false);
            _adaptiveConcurrency.ReportResult(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HTTP request queue item {QueueItemId} failed with an unexpected worker error.", item.Id);
            await RetryOrFailAsync(item, ex.Message, ct).ConfigureAwait(false);
            _adaptiveConcurrency.ReportResult(false);
        }
    }

    private async Task<UrlFetchSnapshot> SendAsync(HttpRequestQueueItem item, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("spider");
        var sw = Stopwatch.StartNew();
        var method = new HttpMethod(item.Method);
        var currentUrl = item.RequestUrl;
        var redirectCount = 0;
        List<UrlRedirectHop> redirectChain = [];
        Dictionary<string, string> reqHeaders = new(StringComparer.OrdinalIgnoreCase);

        for (;;)
        {
            using var request = new HttpRequestMessage(method, currentUrl);
            request.Headers.UserAgent.ParseAdd("ArgusEngine/1.0 NightmareV2-Compatible/1.0");
            reqHeaders = HeadersToDict(request.Headers);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (IsRedirectStatus(response.StatusCode)
                && response.Headers.Location is { } location
                && redirectCount < MaxRedirects
                && TryResolveRedirectUrl(currentUrl, location, out var nextUrl))
            {
                redirectCount++;
                redirectChain.Add(new UrlRedirectHop(
                    currentUrl,
                    nextUrl,
                    (int)response.StatusCode,
                    location.ToString()));
                currentUrl = nextUrl;
                continue;
            }

            sw.Stop();

            var respHeaders = HeadersToDict(response.Headers);
            foreach (var h in response.Content.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);

            var truncatedBody = await BoundedHttpContentReader.ReadAsStringAsync(response.Content, MaxBodyCaptureChars, ct)
                .ConfigureAwait(false);

            var contentType = response.Content.Headers.ContentType?.ToString();

            return new UrlFetchSnapshot(
                method.Method,
                reqHeaders,
                null,
                (int)response.StatusCode,
                respHeaders,
                truncatedBody,
                response.Content.Headers.ContentLength,
                sw.Elapsed.TotalMilliseconds,
                contentType,
                DateTimeOffset.UtcNow,
                currentUrl,
                redirectCount,
                redirectChain);
        }
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        (int)statusCode is 300 or 301 or 302 or 303 or 307 or 308;

    private static bool TryResolveRedirectUrl(string currentUrl, Uri location, out string nextUrl)
    {
        nextUrl = "";
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
            return false;

        var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        if (redirectUri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(redirectUri.Host))
            return false;

        nextUrl = redirectUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        return true;
    }

    private async Task<UrlFetchSnapshot> SaveResponseAsync(
        Guid queueItemId,
        UrlFetchSnapshot snapshot,
        string state,
        string? error,
        bool terminal,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var item = await db.HttpRequestQueue.FirstOrDefaultAsync(q => q.Id == queueItemId, ct).ConfigureAwait(false);
        if (item is null)
            return snapshot;

        if (!stateMachine.CanTransition(HttpRequestQueueState.ToKind(item.State), HttpRequestQueueState.ToKind(state)))
        {
            logger.LogWarning(
                "Invalid queue transition {From} -> {To} for item {QueueItemId}.",
                item.State,
                state,
                queueItemId);
            return snapshot;
        }

        var storedSnapshot = await StoreArtifactsAsync(item, snapshot, ct).ConfigureAwait(false);

        item.State = state;
        item.UpdatedAtUtc = now;
        item.CompletedAtUtc = terminal ? now : item.CompletedAtUtc;
        item.LockedBy = null;
        item.LockedUntilUtc = null;
        item.DurationMs = (long?)storedSnapshot.DurationMs;
        item.LastHttpStatus = storedSnapshot.StatusCode;
        item.LastError = error;
        item.RequestHeadersBlobId = storedSnapshot.RequestHeadersBlobId;
        item.RequestBodyBlobId = storedSnapshot.RequestBodyBlobId;
        item.ResponseHeadersBlobId = storedSnapshot.ResponseHeadersBlobId;
        item.ResponseBodyBlobId = storedSnapshot.ResponseBodyBlobId;
        item.RedirectChainBlobId = storedSnapshot.RedirectChainBlobId;
        item.ResponseBodySha256 = storedSnapshot.ResponseBodySha256;
        item.ResponseBodyPreview = SanitizeForPostgresText(storedSnapshot.ResponseBodyPreview);
        item.ResponseBodyTruncated = storedSnapshot.ResponseBodyTruncated;
        item.RequestHeadersJson = null;
        item.RequestBody = null;
        item.ResponseHeadersJson = null;
        item.ResponseBody = null;
        item.ResponseContentType = SanitizeForPostgresText(storedSnapshot.ContentType);
        item.ResponseContentLength = storedSnapshot.ResponseSizeBytes;
        item.FinalUrl = SanitizeForPostgresText(storedSnapshot.FinalUrl);
        item.RedirectCount = Math.Max(0, storedSnapshot.RedirectCount);
        item.RedirectChainJson = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return storedSnapshot;
    }

    private async Task SaveRetryResponseAsync(HttpRequestQueueItem item, UrlFetchSnapshot snapshot, string error, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Max(0, item.AttemptCount)) * 5));
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.HttpRequestQueue.FirstOrDefaultAsync(q => q.Id == item.Id, ct).ConfigureAwait(false);
        if (row is null)
            return;

        if (!stateMachine.CanTransition(HttpRequestQueueState.ToKind(row.State), HttpRequestQueueStateKind.Retry))
        {
            logger.LogWarning("Invalid queue transition {From} -> Retry for item {QueueItemId}.", row.State, item.Id);
            return;
        }

        var storedSnapshot = await StoreArtifactsAsync(row, snapshot, ct).ConfigureAwait(false);

        row.State = HttpRequestQueueState.Retry;
        row.UpdatedAtUtc = now;
        row.NextAttemptAtUtc = now + delay;
        row.LockedBy = null;
        row.LockedUntilUtc = null;
        row.DurationMs = (long?)storedSnapshot.DurationMs;
        row.LastHttpStatus = storedSnapshot.StatusCode;
        row.LastError = Truncate(error, 2048);
        row.RequestHeadersBlobId = storedSnapshot.RequestHeadersBlobId;
        row.RequestBodyBlobId = storedSnapshot.RequestBodyBlobId;
        row.ResponseHeadersBlobId = storedSnapshot.ResponseHeadersBlobId;
        row.ResponseBodyBlobId = storedSnapshot.ResponseBodyBlobId;
        row.RedirectChainBlobId = storedSnapshot.RedirectChainBlobId;
        row.ResponseBodySha256 = storedSnapshot.ResponseBodySha256;
        row.ResponseBodyPreview = SanitizeForPostgresText(storedSnapshot.ResponseBodyPreview);
        row.ResponseBodyTruncated = storedSnapshot.ResponseBodyTruncated;
        row.RequestHeadersJson = null;
        row.RequestBody = null;
        row.ResponseHeadersJson = null;
        row.ResponseBody = null;
        row.ResponseContentType = SanitizeForPostgresText(storedSnapshot.ContentType);
        row.ResponseContentLength = storedSnapshot.ResponseSizeBytes;
        row.FinalUrl = SanitizeForPostgresText(storedSnapshot.FinalUrl);
        row.RedirectCount = Math.Max(0, storedSnapshot.RedirectCount);
        row.RedirectChainJson = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<UrlFetchSnapshot> StoreArtifactsAsync(
        HttpRequestQueueItem item,
        UrlFetchSnapshot snapshot,
        CancellationToken ct)
    {
        var requestHeaders = await artifactStore.StoreTextAsync(
            item.TargetId,
            item.AssetId,
            "request_headers",
            "application/json",
            JsonSerializer.Serialize(snapshot.RequestHeaders, JsonOptions),
            ct).ConfigureAwait(false);

        var requestBody = await artifactStore.StoreTextAsync(
            item.TargetId,
            item.AssetId,
            "request_body",
            "text/plain; charset=utf-8",
            snapshot.RequestBody,
            ct).ConfigureAwait(false);

        var responseHeaders = await artifactStore.StoreTextAsync(
            item.TargetId,
            item.AssetId,
            "response_headers",
            "application/json",
            JsonSerializer.Serialize(snapshot.ResponseHeaders, JsonOptions),
            ct).ConfigureAwait(false);

        var responseBody = await artifactStore.StoreTextAsync(
            item.TargetId,
            item.AssetId,
            "response_body",
            snapshot.ContentType,
            snapshot.ResponseBody,
            ct).ConfigureAwait(false);

        var redirectChain = await artifactStore.StoreTextAsync(
            item.TargetId,
            item.AssetId,
            "redirect_chain",
            "application/json",
            SerializeRedirectChain(snapshot),
            ct).ConfigureAwait(false);

        return snapshot with
        {
            RequestHeadersBlobId = requestHeaders?.BlobId,
            RequestBodyBlobId = requestBody?.BlobId,
            ResponseHeadersBlobId = responseHeaders?.BlobId,
            ResponseBodyBlobId = responseBody?.BlobId,
            RedirectChainBlobId = redirectChain?.BlobId,
            ResponseBodySha256 = responseBody?.Sha256,
            ResponseBodyPreview = responseBody?.Preview,
            ResponseBodyTruncated = responseBody?.Truncated ?? false,
            ResponseBody = null
        };
    }

    private static string? SerializeRedirectChain(UrlFetchSnapshot snapshot) =>
        snapshot.RedirectChain is { Count: > 0 }
            ? JsonSerializer.Serialize(snapshot.RedirectChain, JsonOptions)
            : null;

    private async Task RetryOrFailAsync(HttpRequestQueueItem item, string error, CancellationToken ct)
    {
        var terminal = item.AttemptCount >= item.MaxAttempts;
        if (terminal)
        {
            await MarkFailedAsync(item.Id, error, terminal: true, ct).ConfigureAwait(false);

            using var scope = scopeFactory.CreateScope();
            var persistence = scope.ServiceProvider.GetRequiredService<IAssetPersistence>();
            var snapshot = new UrlFetchSnapshot(
                item.Method,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null,
                0,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null,
                null,
                0,
                "text/plain",
                DateTimeOffset.UtcNow,
                item.RequestUrl,
                ResponseBodyPreview: Truncate(error, Math.Min(error.Length, 4096)));

            await persistence.ConfirmUrlAssetAsync(item.AssetId, snapshot, Guid.Empty, ct).ConfigureAwait(false);
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

    private static bool ShouldQueueRetry(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

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

    private static string? SanitizeForPostgresText(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\0", string.Empty, StringComparison.Ordinal);

    private static string TruncateDiscoveryContext(string s, int maxChars = 512) =>
        s.Length <= maxChars ? s : s[..(maxChars - 1)] + "…";
}
