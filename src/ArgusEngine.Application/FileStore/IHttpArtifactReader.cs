namespace ArgusEngine.Application.FileStore;

public interface IHttpArtifactReader
{
    Task<string?> ReadTextAsync(Guid blobId, int? maxBytes, CancellationToken ct);
    Task<byte[]?> ReadBytesAsync(Guid blobId, int? maxBytes, CancellationToken ct);
}
