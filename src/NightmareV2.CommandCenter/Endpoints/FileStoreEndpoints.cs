using NightmareV2.Application.FileStore;

namespace NightmareV2.CommandCenter.Endpoints;

public static class FileStoreEndpoints
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    public static IEndpointRouteBuilder MapFileStoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/api/filestore",
                async (HttpRequest req, IFileStore store, CancellationToken ct) =>
                {
                    if (!req.HasFormContentType)
                        return Results.BadRequest("multipart/form-data with field \"file\" is required");
                    var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
                    var file = form.Files.GetFile("file");
                    if (file is null || file.Length == 0)
                        return Results.BadRequest("multipart field \"file\" is required");
                    if (file.Length > MaxUploadBytes)
                        return Results.BadRequest($"file exceeds maximum size ({MaxUploadBytes} bytes)");
                    var logical = form["logicalName"].ToString();
                    if (string.IsNullOrWhiteSpace(logical))
                        logical = file.FileName;
                    await using var uploadStream = file.OpenReadStream();
                    var created = await store.StoreAsync(uploadStream, file.ContentType, logical, ct).ConfigureAwait(false);
                    return Results.Created($"/api/filestore/{created.Id}", created);
                })
            .WithName("UploadFileBlob")
            .DisableAntiforgery();

        app.MapGet(
                "/api/filestore/{id:guid}",
                async (Guid id, IFileStore store, CancellationToken ct) =>
                {
                    var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
                    return meta is null ? Results.NotFound() : Results.Ok(meta);
                })
            .WithName("GetFileBlobInfo");

        app.MapGet(
                "/api/filestore/{id:guid}/download",
                async (Guid id, IFileStore store, CancellationToken ct) =>
                {
                    var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
                    if (meta is null)
                        return Results.NotFound();
                    var stream = await store.OpenReadAsync(id, ct).ConfigureAwait(false);
                    if (stream is null)
                        return Results.NotFound();
                    return Results.File(
                        stream,
                        meta.ContentType ?? "application/octet-stream",
                        fileDownloadName: meta.LogicalName ?? $"{meta.Id:N}");
                })
            .WithName("DownloadFileBlob");

        app.MapDelete(
                "/api/filestore/{id:guid}",
                async (Guid id, IFileStore store, CancellationToken ct) =>
                {
                    var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
                    if (meta is null)
                        return Results.NotFound();
                    await store.DeleteAsync(id, ct).ConfigureAwait(false);
                    return Results.NoContent();
                })
            .WithName("DeleteFileBlob");

        return app;
    }

    public static void Map(WebApplication app) => app.MapFileStoreEndpoints();
}
