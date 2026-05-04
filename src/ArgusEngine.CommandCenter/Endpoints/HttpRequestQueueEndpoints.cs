using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using ArgusEngine.CommandCenter.Hubs;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Realtime;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class HttpRequestQueueEndpoints
{
    public static IEndpointRouteBuilder MapHttpRequestQueueEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/http-request-queue/settings",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var row = await db.HttpRequestQueueSettings.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == 1, ct)
                        .ConfigureAwait(false)
                        ?? new HttpRequestQueueSettings();

                    return Results.Ok(
                        new HttpRequestQueueSettingsDto(
                            row.Enabled,
                            row.GlobalRequestsPerMinute,
                            row.PerDomainRequestsPerMinute,
                            row.MaxConcurrency,
                            row.RequestTimeoutSeconds,
                            row.UpdatedAtUtc));
                })
            .WithName("GetHttpRequestQueueSettings");

        app.MapPut(
                "/api/http-request-queue/settings",
                async (HttpRequestQueueSettingsPatch body, ArgusDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
                {
                    var row = await db.HttpRequestQueueSettings.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new HttpRequestQueueSettings { Id = 1 };
                        db.HttpRequestQueueSettings.Add(row);
                    }

                    row.Enabled = body.Enabled;
                    row.GlobalRequestsPerMinute = Math.Clamp(body.GlobalRequestsPerMinute, 1, 100_000);
                    row.PerDomainRequestsPerMinute = Math.Clamp(body.PerDomainRequestsPerMinute, 1, 10_000);
                    row.MaxConcurrency = Math.Clamp(body.MaxConcurrency, 1, 1_000);
                    row.RequestTimeoutSeconds = Math.Clamp(body.RequestTimeoutSeconds, 5, 300);
                    row.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await hub.Clients.All.SendAsync(
                            DiscoveryHubEvents.DomainEvent,
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

        return app;
    }

    public static void Map(WebApplication app) => app.MapHttpRequestQueueEndpoints();
}
