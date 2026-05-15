using ArgusEngine.CommandCenter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using MassTransit;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class HttpRequestQueueEndpoints
{
    public static IEndpointRouteBuilder MapHttpRequestQueueEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/http-request-queue/settings",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    HttpRequestQueueSettings row;
                    try
                    {
                        row = await db.HttpRequestQueueSettings.AsNoTracking()
                            .FirstOrDefaultAsync(s => s.Id == 1, ct)
                            .ConfigureAwait(false)
                            ?? new HttpRequestQueueSettings();
                    }
                    catch
                    {
                        row = new HttpRequestQueueSettings();
                    }

                    return Results.Ok(row);
                })
            .WithName("GetHttpRequestQueueSettings");

        app.MapPut(
                "/api/http-request-queue/settings",
                async (HttpRequestQueueSettings body, ArgusDbContext db, IPublishEndpoint publishEndpoint, CancellationToken ct) =>
                {
                    var row = await db.HttpRequestQueueSettings.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new HttpRequestQueueSettings { Id = 1 };
                        db.HttpRequestQueueSettings.Add(row);
                    }

                    row.Enabled = body.Enabled;
                    row.GlobalRequestsPerMinute = Math.Clamp(body.GlobalRequestsPerMinute, 1, 120_000);
                    row.PerDomainRequestsPerMinute = Math.Clamp(body.PerDomainRequestsPerMinute, 1, 10_000);
                    row.MaxConcurrency = Math.Clamp(body.MaxConcurrency, 1, 1_000);
                    row.RequestTimeoutSeconds = Math.Clamp(body.RequestTimeoutSeconds, 5, 300);
                    row.RotateUserAgents = body.RotateUserAgents;
                    row.CustomUserAgentsJson = body.CustomUserAgentsJson;
                    row.RandomizeHeaderOrder = body.RandomizeHeaderOrder;
                    row.UseRandomJitter = body.UseRandomJitter;
                    row.MinJitterMs = Math.Clamp(body.MinJitterMs, 0, 60_000);
                    row.MaxJitterMs = Math.Clamp(body.MaxJitterMs, row.MinJitterMs, 60_000);
                    row.SpoofReferer = body.SpoofReferer;
                    row.CustomHeadersJson = body.CustomHeadersJson;
                    row.ProxyRoutingEnabled = body.ProxyRoutingEnabled;
                    row.ProxyStickySubdomainsEnabled = body.ProxyStickySubdomainsEnabled;
                    row.ProxyAssignmentSalt = string.IsNullOrWhiteSpace(body.ProxyAssignmentSalt)
                        ? "argus-proxy-v1"
                        : body.ProxyAssignmentSalt.Trim();
                    row.ProxyServersJson = body.ProxyServersJson;
                    row.ProxyFingerprintingEnabled = body.ProxyFingerprintingEnabled;
                    row.ProxyFingerprintMinDelayMs = Math.Clamp(body.ProxyFingerprintMinDelayMs, 0, 60_000);
                    row.ProxyFingerprintMaxDelayMs = Math.Clamp(
                        body.ProxyFingerprintMaxDelayMs,
                        row.ProxyFingerprintMinDelayMs,
                        120_000);
                    row.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await publishEndpoint.Publish(
                            new LiveUiEventDto(
                                "QueueSettingsChanged",
                                null,
                                null,
                                "http",
                                body.Enabled ? "HTTP queue enabled" : "HTTP queue disabled",
                                row.UpdatedAtUtc),
                            cancellationToken: ct)
                        .ConfigureAwait(false);
                    return Results.NoContent();
                })
            .WithName("UpdateHttpRequestQueueSettings");

        app.MapGet(
                "/api/http-request-queue",
                async (ArgusDbContext db, Guid? targetId, string? state, int? take, CancellationToken ct) =>
                {
                    var q = db.HttpRequestQueue.AsNoTracking().AsQueryable();
                    if (targetId is { } tid)
                        q = q.Where(r => r.TargetId == tid);
                    if (!string.IsNullOrWhiteSpace(state))
                    {
                        var requestedState = state.Trim();
                        q = q.Where(r => r.State == requestedState);
                    }

                    IQueryable<HttpRequestQueueItem> ordered = q.OrderByDescending(r => r.CreatedAtUtc);
                    if (take is > 0)
                        ordered = ordered.Take(Math.Clamp(take.Value, 1, 100_000));

                    var rows = await ordered
                        .Select(r => new HttpRequestQueueRowDto(
                            r.Id,
                            r.AssetId,
                            r.TargetId,
                            r.AssetKind.ToString(),
                            r.Method,
                            r.RequestUrl,
                            r.DomainKey,
                            r.State.ToString(),
                            r.AttemptCount,
                            r.MaxAttempts,
                            r.Priority,
                            r.CreatedAtUtc,
                            r.UpdatedAtUtc,
                            r.NextAttemptAtUtc,
                            r.StartedAtUtc,
                            r.CompletedAtUtc,
                            r.LockedBy,
                            r.LockedUntilUtc,
                            r.LastHttpStatus,
                            r.LastError,
                            r.RequestHeadersJson,
                            r.RequestBody,
                            r.ResponseHeadersJson,
                            r.ResponseBody,
                            r.DurationMs,
                            r.ResponseContentType,
                            r.ResponseContentLength,
                            r.FinalUrl,
                            r.RedirectCount,
                            r.RedirectChainJson))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListHttpRequestQueue");

        app.MapGet(
                "/api/http-request-queue/metrics",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var oneMinuteAgo = now.AddMinutes(-1);
                    var oneHourAgo = now.AddHours(-1);
                    var oneDayAgo = now.AddHours(-24);

                    var queued = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Queued, ct)
                        .ConfigureAwait(false);
                    var retry = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now, ct)
                        .ConfigureAwait(false);
                    var scheduledRetry = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc > now, ct)
                        .ConfigureAwait(false);
                    var inFlight = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.InFlight, ct)
                        .ConfigureAwait(false);
                    var failed = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed, ct)
                        .ConfigureAwait(false);
                    var completedLastHour = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Succeeded && q.CompletedAtUtc >= oneHourAgo, ct)
                        .ConfigureAwait(false);
                    var failedLastMinute = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= oneMinuteAgo, ct)
                        .ConfigureAwait(false);
                    var failedLastHour = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= oneHourAgo, ct)
                        .ConfigureAwait(false);
                    var failedLast24Hours = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= oneDayAgo, ct)
                        .ConfigureAwait(false);
                    var sentLastMinute = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.StartedAtUtc >= oneMinuteAgo, ct)
                        .ConfigureAwait(false);
                    var sentLastHour = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.StartedAtUtc >= oneHourAgo, ct)
                        .ConfigureAwait(false);
                    var sentLast24Hours = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.StartedAtUtc >= oneDayAgo, ct)
                        .ConfigureAwait(false);
                    var oldestQueuedAt = await db.HttpRequestQueue.AsNoTracking()
                        .Where(q => q.State == HttpRequestQueueState.Queued)
                        .OrderBy(q => q.CreatedAtUtc)
                        .Select(q => (DateTimeOffset?)q.CreatedAtUtc)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(
                        new HttpRequestQueueMetricsDto(
                            queued,
                            retry,
                            scheduledRetry,
                            inFlight,
                            failed,
                            completedLastHour,
                            queued,
                            oldestQueuedAt,
                            oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds,
                            failedLastMinute,
                            failedLastHour,
                            failedLast24Hours,
                            sentLastMinute,
                            sentLastHour,
                            sentLast24Hours));
                })
            .WithName("GetHttpRequestQueueMetrics");

        app.MapPost(
                "/api/http-request-queue/retry",
                async (HttpRequestQueueIdsRequest body, ArgusDbContext db, CancellationToken ct) =>
                {
                    var ids = NormalizeIds(body.Ids);
                    if (ids.Length == 0)
                        return Results.BadRequest("ids is required.");

                    var now = DateTimeOffset.UtcNow;
                    var affected = await db.HttpRequestQueue
                        .Where(q => ids.Contains(q.Id))
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(q => q.State, HttpRequestQueueState.Queued)
                                .SetProperty(q => q.NextAttemptAtUtc, (DateTimeOffset?)null)
                                .SetProperty(q => q.StartedAtUtc, (DateTimeOffset?)null)
                                .SetProperty(q => q.CompletedAtUtc, (DateTimeOffset?)null)
                                .SetProperty(q => q.LockedBy, (string?)null)
                                .SetProperty(q => q.LockedUntilUtc, (DateTimeOffset?)null)
                                .SetProperty(q => q.LastError, (string?)null)
                                .SetProperty(q => q.UpdatedAtUtc, now),
                            ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new HttpRequestQueueMutationResult("retry-selected", affected));
                })
            .WithName("RetrySelectedHttpRequestQueueRows");

        app.MapPost(
                "/api/http-request-queue/clear-selected",
                async (HttpRequestQueueIdsRequest body, ArgusDbContext db, CancellationToken ct) =>
                {
                    var ids = NormalizeIds(body.Ids);
                    if (ids.Length == 0)
                        return Results.BadRequest("ids is required.");

                    var affected = await db.HttpRequestQueue
                        .Where(q => ids.Contains(q.Id))
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new HttpRequestQueueMutationResult("clear-selected", affected));
                })
            .WithName("ClearSelectedHttpRequestQueueRows");

        app.MapPost(
                "/api/http-request-queue/clear-completed",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var affected = await db.HttpRequestQueue
                        .Where(q => q.State == HttpRequestQueueState.Succeeded)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new HttpRequestQueueMutationResult("clear-completed", affected));
                })
            .WithName("ClearCompletedHttpRequestQueueRows");

        app.MapPost(
                "/api/http-request-queue/clear-failed",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var affected = await db.HttpRequestQueue
                        .Where(q => q.State == HttpRequestQueueState.Failed)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new HttpRequestQueueMutationResult("clear-failed", affected));
                })
            .WithName("ClearFailedHttpRequestQueueRows");

        app.MapPost(
                "/api/http-request-queue/clear-older-than",
                async (HttpRequestQueueClearOlderThanRequest body, ArgusDbContext db, CancellationToken ct) =>
                {
                    var affected = await db.HttpRequestQueue
                        .Where(q => q.CreatedAtUtc < body.CutoffUtc)
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new HttpRequestQueueMutationResult("clear-older-than-selected", affected));
                })
            .WithName("ClearHttpRequestQueueRowsOlderThanSelected");

        return app;
    }

    private static Guid[] NormalizeIds(IReadOnlyList<Guid>? ids) =>
        ids is null ? [] : ids.Where(id => id != Guid.Empty).Distinct().ToArray();

    public static void Map(WebApplication app) => app.MapHttpRequestQueueEndpoints();
}




