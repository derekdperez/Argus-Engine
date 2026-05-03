using Microsoft.EntityFrameworkCore;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.Infrastructure.Persistence.Data;

public sealed class FileStoreDbContext(DbContextOptions<FileStoreDbContext> options) : DbContext(options)
{
    public DbSet<StoredFileBlob> Blobs => Set<StoredFileBlob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFileBlob>(e =>
        {
            e.ToTable("stored_file_blobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ContentType).HasMaxLength(256);
            e.Property(x => x.LogicalName).HasMaxLength(1024);
            e.Property(x => x.Sha256Hex).HasMaxLength(64).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.HasIndex(x => x.Sha256Hex);
            e.HasIndex(x => x.CreatedAtUtc);
        });
    }
}
