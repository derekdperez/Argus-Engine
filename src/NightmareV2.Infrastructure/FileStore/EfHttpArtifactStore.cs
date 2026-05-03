using System.Text;

using Microsoft.Extensions.Options;

using NightmareV2.Application.FileStore;

namespace NightmareV2.Infrastructure.FileStore;

public sealed class EfHttpArtifactStore(
    IFileStore fileStore,
    IOptions<HttpArtifactOptions> options) : IHttpArtifactStore, IHttpArtifactReader
{
    public Task<HttpArtifactRef?> StoreTextAsync(
        Guid targetId,
        Guid assetId,
        string artifactKind,
        string? contentType,
        string? content,
        CancellationToken ct)
    {
        if (content is null)
            return Task.FromResult<HttpArtifactRef?>(null);

        return StoreBytesAsync(
            targetId,
            assetId,
            artifactKind,
            contentType ?? "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(content),
            ct);
    }

    public async Task<HttpArtifactRef?> StoreBytesAsync(
        Guid targetId,
        Guid assetId,
        string artifactKind,
        string? contentType,
        byte[]? content,
        CancellationToken ct)
    {
        if (content is null)
            return null;

        if (!ShouldStore(artifactKind))
            return null;

        var opt = options.Value;
        var truncated = content.Length > opt.MaxStoredResponseBodyBytes;
        var stored = truncated
            ? content.AsSpan(0, opt.MaxStoredResponseBodyBytes).ToArray()
            : content;

        await using var stream = new MemoryStream(stored, writable: false);
        var descriptor = await fileStore.StoreAsync(
            stream,
            contentType,
            LogicalName(targetId, assetId, artifactKind),
            ct).ConfigureAwait(false);

        return new HttpArtifactRef(
            descriptor.Id,
            descriptor.Sha256Hex,
            descriptor.ContentLength,
            truncated,
            BuildPreview(stored, opt.MaxPreviewChars));
    }

    public async Task<string?> ReadTextAsync(Guid blobId, int? maxBytes, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(blobId, maxBytes, ct).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]?> ReadBytesAsync(Guid blobId, int? maxBytes, CancellationToken ct)
    {
        await using var stream = await fileStore.OpenReadAsync(blobId, ct).ConfigureAwait(false);
        if (stream is null)
            return null;

        var limit = Math.Max(0, maxBytes ?? int.MaxValue);
        await using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        while (buffer.Length < limit)
        {
            var toRead = Math.Min(chunk.Length, limit - (int)buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private bool ShouldStore(string artifactKind)
    {
        var opt = options.Value;

        return artifactKind switch
        {
            "request_headers" => opt.StoreRequestHeaders,
            "response_headers" => opt.StoreResponseHeaders,
            "response_body" => opt.StoreResponseBodies,
            _ => true
        };
    }

    private static string LogicalName(Guid targetId, Guid assetId, string artifactKind) =>
        $"http/{targetId:N}/{assetId:N}/{artifactKind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";

    private static string? BuildPreview(byte[] bytes, int maxPreviewChars)
    {
        if (bytes.Length == 0 || maxPreviewChars <= 0)
            return null;

        var text = Encoding.UTF8.GetString(bytes);
        return text.Length <= maxPreviewChars ? text : text[..maxPreviewChars];
    }
}
