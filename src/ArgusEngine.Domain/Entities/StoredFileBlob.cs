namespace ArgusEngine.Domain.Entities;

/// <summary>Binary payload stored in the dedicated file-store database (not the main recon/event Postgres DB).</summary>
public sealed class StoredFileBlob
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? ContentType { get; set; }
    public string? LogicalName { get; set; }
    public long ContentLength { get; set; }
    public string Sha256Hex { get; set; } = "";
    public byte[] Content { get; set; } = Array.Empty<byte>();
}
