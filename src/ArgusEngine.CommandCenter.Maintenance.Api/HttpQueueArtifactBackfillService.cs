using ArgusEngine.Application.FileStore;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.Maintenance.Api;

public sealed class HttpQueueArtifactBackfillService(
    ArgusDbContext db,
    IHttpArtifactStore artifactStore)
{
    private const int BatchSize = 500;

    public async Task<HttpQueueArtifactBackfillResult> RunOnceAsync(CancellationToken ct)
    {
        var rows = await db.HttpRequestQueue
            .Where(row =>
                (row.RequestHeadersJson != null && row.RequestHeadersBlobId == null)
                || (row.RequestBody != null && row.RequestBodyBlobId == null)
                || (row.ResponseHeadersJson != null && row.ResponseHeadersBlobId == null)
                || (row.ResponseBody != null && row.ResponseBodyBlobId == null)
                || (row.RedirectChainJson != null && row.RedirectChainBlobId == null))
            .OrderBy(row => row.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var artifactsStored = 0;
        foreach (var row in rows)
        {
            if (row.RequestHeadersJson is not null && row.RequestHeadersBlobId is null)
            {
                var artifact = await artifactStore.StoreTextAsync(row.TargetId, row.AssetId, "request_headers", "application/json", row.RequestHeadersJson, ct)
                    .ConfigureAwait(false);
                row.RequestHeadersBlobId = artifact?.BlobId;
                artifactsStored += artifact is null ? 0 : 1;
            }

            if (row.RequestBody is not null && row.RequestBodyBlobId is null)
            {
                var artifact = await artifactStore.StoreTextAsync(row.TargetId, row.AssetId, "request_body", "text/plain; charset=utf-8", row.RequestBody, ct)
                    .ConfigureAwait(false);
                row.RequestBodyBlobId = artifact?.BlobId;
                artifactsStored += artifact is null ? 0 : 1;
            }

            if (row.ResponseHeadersJson is not null && row.ResponseHeadersBlobId is null)
            {
                var artifact = await artifactStore.StoreTextAsync(row.TargetId, row.AssetId, "response_headers", "application/json", row.ResponseHeadersJson, ct)
                    .ConfigureAwait(false);
                row.ResponseHeadersBlobId = artifact?.BlobId;
                artifactsStored += artifact is null ? 0 : 1;
            }

            if (row.ResponseBody is not null && row.ResponseBodyBlobId is null)
            {
                var artifact = await artifactStore.StoreTextAsync(row.TargetId, row.AssetId, "response_body", row.ResponseContentType, row.ResponseBody, ct)
                    .ConfigureAwait(false);
                row.ResponseBodyBlobId = artifact?.BlobId;
                row.ResponseBodySha256 = artifact?.Sha256;
                row.ResponseBodyPreview = artifact?.Preview;
                row.ResponseBodyTruncated = artifact?.Truncated ?? false;
                artifactsStored += artifact is null ? 0 : 1;
            }

            if (row.RedirectChainJson is not null && row.RedirectChainBlobId is null)
            {
                var artifact = await artifactStore.StoreTextAsync(row.TargetId, row.AssetId, "redirect_chain", "application/json", row.RedirectChainJson, ct)
                    .ConfigureAwait(false);
                row.RedirectChainBlobId = artifact?.BlobId;
                artifactsStored += artifact is null ? 0 : 1;
            }

            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new HttpQueueArtifactBackfillResult(rows.Count, artifactsStored, rows.Count == BatchSize);
    }
}

public sealed record HttpQueueArtifactBackfillResult(
    int RowsScanned,
    int ArtifactsStored,
    bool MoreRowsAvailable);
