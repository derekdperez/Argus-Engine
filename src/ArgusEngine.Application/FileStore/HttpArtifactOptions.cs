namespace ArgusEngine.Application.FileStore;

public sealed class HttpArtifactOptions
{
    public int MaxStoredResponseBodyBytes { get; set; } = 2_000_000;
    public int MaxPreviewChars { get; set; } = 4096;
    public bool StoreRequestHeaders { get; set; } = true;
    public bool StoreResponseHeaders { get; set; } = true;
    public bool StoreResponseBodies { get; set; } = true;
}
