namespace ArgusEngine.Application.FileStore;

public sealed record HttpArtifactRef(
    Guid BlobId,
    string Sha256,
    long SizeBytes,
    bool Truncated,
    string? Preview);
