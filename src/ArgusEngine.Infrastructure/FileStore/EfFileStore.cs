using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.FileStore;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Persistence.Data;

namespace ArgusEngine.Infrastructure.FileStore;

public sealed class EfFileStore(IDbContextFactory<FileStoreDbContext> dbFactory) : IFileStore
{
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Blobs.Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileBlobDescriptor?> GetDescriptorAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Blobs.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new FileBlobDescriptor(b.Id, b.CreatedAtUtc, b.ContentLength, b.ContentType, b.LogicalName, b.Sha256Hex))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row;
    }

    public async Task<Stream?> OpenReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await db.Blobs.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => b.Content)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
            return null;
        return new MemoryStream(bytes, writable: false);
    }

    public async Task<FileBlobDescriptor> StoreAsync(
        Stream content,
        string? contentType,
        string? logicalName,
        CancellationToken cancellationToken = default)
    {
        await using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var row = new StoredFileBlob
        {
            Id = id,
            CreatedAtUtc = now,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType.Trim(),
            LogicalName = string.IsNullOrWhiteSpace(logicalName) ? null : logicalName.Trim(),
            ContentLength = bytes.LongLength,
            Sha256Hex = hash,
            Content = bytes,
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Blobs.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new FileBlobDescriptor(id, now, bytes.LongLength, row.ContentType, row.LogicalName, hash);
    }
}
