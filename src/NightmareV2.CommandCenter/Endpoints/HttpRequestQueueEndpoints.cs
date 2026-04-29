using Microsoft.EntityFrameworkCore;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class HttpRequestQueueEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/http-request-queue/settings",
                async (NightmareDbContext db, CancellationToken ct) =>
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
                async (HttpRequestQueueSettingsPatch body, NightmareDbContext db, CancellationToken ct) =>
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
                    return Results.NoContent();
                })
            .WithName("UpdateHttpRequestQueueSettings");

        app.MapGet(
                "/api/http-request-queue",
                async (NightmareDbContext db, Guid? targetId, int? take, CancellationToken ct) =>
                {
                    var limit = Math.Clamp(take ?? 800, 1, 5000);
                    var q = db.HttpRequestQueue.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).AsQueryable();
                    if (targetId is { } tid)
                        q = q.Where(r => r.TargetId == tid);

                    var rows = await q.Take(limit)
                        .Select(r => new HttpRequestQueueRowDto(
                            r.Id,
                            r.AssetId,
                            r.TargetId,
                            r.AssetKind.ToString(),
                            r.Method,
                            r.RequestUrl,
                            r.DomainKey,
                            r.State,
                            r.AttemptCount,
                            r.MaxAttempts,
                            r.Priority,
                            r.CreatedAtUtc,
                            r.UpdatedAtUtc,
                            r.NextAttemptAtUtc,
                            r.StartedAtUtc,
                            r.CompletedAtUtc,
                            r.LastHttpStatus,
                            r.LastError,
                            r.DurationMs,
                            r.ResponseContentType,
                            r.ResponseContentLength,
                            r.FinalUrl))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListHttpRequestQueue");

        app.MapGet(
                "/api/http-request-queue/metrics",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var oneHourAgo = now.AddHours(-1);

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
                    var oldestQueuedAt = await db.HttpRequestQueue.AsNoTracking()
                        .Where(q => q.State == HttpRequestQueueState.Queued
                            || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now))
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
                            queued + retry,
                            oldestQueuedAt,
                            oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds));
                })
            .WithName("GetHttpRequestQueueMetrics");
    }
}
