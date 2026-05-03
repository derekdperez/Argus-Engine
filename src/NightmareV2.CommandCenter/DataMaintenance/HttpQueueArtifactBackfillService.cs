using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.FileStore;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.DataMaintenance;

public sealed class HttpQueueArtifactBackfillService(
    IDbContextFactory<NightmareDbContext> dbFactory,
    IHttpArtifactStore artifactStore)
{
    private const int BatchSize = 500;

    public async Task<HttpQueueArtifactBackfillResult> RunOnceAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await db.HttpRequestQueue
            .Where(q =>
                (q.ResponseBody != null && q.ResponseBodyBlobId == null)
                || (q.ResponseHeadersJson != null && q.ResponseHeadersBlobId == null)
                || (q.RedirectChainJson != null && q.RedirectChainBlobId == null)
                || (q.RequestHeadersJson != null && q.RequestHeadersBlobId == null)
                || (q.RequestBody != null && q.RequestBodyBlobId == null))
            .OrderBy(q => q.CompletedAtUtc ?? q.UpdatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            await BackfillRowAsync(row, ct).ConfigureAwait(false);
        }

        if (rows.Count > 0)
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var remainingEstimate = await db.HttpRequestQueue
            .LongCountAsync(q =>
                (q.ResponseBody != null && q.ResponseBodyBlobId == null)
                || (q.ResponseHeadersJson != null && q.ResponseHeadersBlobId == null)
                || (q.RedirectChainJson != null && q.RedirectChainBlobId == null)
                || (q.RequestHeadersJson != null && q.RequestHeadersBlobId == null)
                || (q.RequestBody != null && q.RequestBodyBlobId == null),
                ct)
            .ConfigureAwait(false);

        return new HttpQueueArtifactBackfillResult(rows.Count, remainingEstimate, DateTimeOffset.UtcNow);
    }

    private async Task BackfillRowAsync(HttpRequestQueueItem row, CancellationToken ct)
    {
        var requestHeaders = await artifactStore.StoreTextAsync(
            row.TargetId,
            row.AssetId,
            "request_headers",
            "application/json",
            row.RequestHeadersJson,
            ct).ConfigureAwait(false);

        var requestBody = await artifactStore.StoreTextAsync(
            row.TargetId,
            row.AssetId,
            "request_body",
            "text/plain; charset=utf-8",
            row.RequestBody,
            ct).ConfigureAwait(false);

        var responseHeaders = await artifactStore.StoreTextAsync(
            row.TargetId,
            row.AssetId,
            "response_headers",
            "application/json",
            row.ResponseHeadersJson,
            ct).ConfigureAwait(false);

        var responseBody = await artifactStore.StoreTextAsync(
            row.TargetId,
            row.AssetId,
            "response_body",
            row.ResponseContentType,
            row.ResponseBody,
            ct).ConfigureAwait(false);

        var redirectChain = await artifactStore.StoreTextAsync(
            row.TargetId,
            row.AssetId,
            "redirect_chain",
            "application/json",
            NormalizeJsonOrNull(row.RedirectChainJson),
            ct).ConfigureAwait(false);

        row.RequestHeadersBlobId ??= requestHeaders?.BlobId;
        row.RequestBodyBlobId ??= requestBody?.BlobId;
        row.ResponseHeadersBlobId ??= responseHeaders?.BlobId;
        row.ResponseBodyBlobId ??= responseBody?.BlobId;
        row.RedirectChainBlobId ??= redirectChain?.BlobId;

        row.ResponseBodySha256 ??= responseBody?.Sha256;
        row.ResponseBodyPreview ??= responseBody?.Preview;
        row.ResponseBodyTruncated = responseBody?.Truncated ?? row.ResponseBodyTruncated;

        if (requestHeaders is not null)
            row.RequestHeadersJson = null;
        if (requestBody is not null)
            row.RequestBody = null;
        if (responseHeaders is not null)
            row.ResponseHeadersJson = null;
        if (responseBody is not null)
            row.ResponseBody = null;
        if (redirectChain is not null)
            row.RedirectChainJson = null;
    }

    private static string? NormalizeJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch
        {
            return JsonSerializer.Serialize(new[] { json });
        }
    }
}

public sealed record HttpQueueArtifactBackfillResult(
    int Processed,
    long RemainingEstimate,
    DateTimeOffset LastRunAtUtc);
