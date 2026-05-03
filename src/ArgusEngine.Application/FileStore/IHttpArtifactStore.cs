namespace ArgusEngine.Application.FileStore;

public interface IHttpArtifactStore
{
    Task<HttpArtifactRef?> StoreTextAsync(
        Guid targetId,
        Guid assetId,
        string artifactKind,
        string? contentType,
        string? content,
        CancellationToken ct);

    Task<HttpArtifactRef?> StoreBytesAsync(
        Guid targetId,
        Guid assetId,
        string artifactKind,
        string? contentType,
        byte[]? content,
        CancellationToken ct);
}
