namespace ArgusEngine.Application.FileStore;

public sealed record FileBlobDescriptor(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    long ContentLength,
    string? ContentType,
    string? LogicalName,
    string Sha256Hex);

public interface IFileStore
{
    Task<FileBlobDescriptor> StoreAsync(Stream content, string? contentType, string? logicalName, CancellationToken cancellationToken = default);

    Task<FileBlobDescriptor?> GetDescriptorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
