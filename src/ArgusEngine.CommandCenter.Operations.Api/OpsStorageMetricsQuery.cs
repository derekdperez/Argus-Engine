using System.Data;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.Operations.Api;

internal static class OpsStorageMetricsQuery
{
    public static async Task<OpsStorageMetrics> LoadAsync(
        ArgusDbContext db,
        IDbContextFactory<FileStoreDbContext> fileStoreFactory,
        CancellationToken ct)
    {
        var assetMetadataBytes = await ExecuteScalarLongAsync(
                db,
                """
                SELECT COALESCE(SUM(
                    octet_length(COALESCE(canonical_key, '')) +
                    octet_length(COALESCE(raw_value, '')) +
                    octet_length(COALESCE(display_name, '')) +
                    octet_length(COALESCE(discovered_by, '')) +
                    octet_length(COALESCE(discovery_context, '')) +
                    octet_length(COALESCE(type_details_json, '')) +
                    octet_length(COALESCE(final_url, '')) +
                    octet_length(COALESCE(redirect_chain_json, ''))
                ), 0)
                FROM stored_assets;
                """,
                ct)
            .ConfigureAwait(false);

        var inlineHttpBytes = await ExecuteScalarLongAsync(
                db,
                """
                SELECT COALESCE(SUM(
                    octet_length(COALESCE(request_headers_json, '')) +
                    octet_length(COALESCE(request_body, '')) +
                    octet_length(COALESCE(response_headers_json, '')) +
                    octet_length(COALESCE(response_body, '')) +
                    octet_length(COALESCE(response_body_preview, '')) +
                    octet_length(COALESCE(redirect_chain_json, ''))
                ), 0)
                FROM http_request_queue;
                """,
                ct)
            .ConfigureAwait(false);

        var eventJournalBytes = await ExecuteScalarLongAsync(
                db,
                """
                SELECT COALESCE(SUM(
                    octet_length(COALESCE(payload_json, '')) +
                    octet_length(COALESCE(message_type, '')) +
                    octet_length(COALESCE(consumer_type, '')) +
                    octet_length(COALESCE(host_name, ''))
                ), 0)
                FROM bus_journal;
                """,
                ct)
            .ConfigureAwait(false);

        eventJournalBytes += await ExecuteScalarLongAsync(
                db,
                """
                SELECT COALESCE(SUM(
                    octet_length(COALESCE(payload_json, '')) +
                    octet_length(COALESCE(message_type, '')) +
                    octet_length(COALESCE(producer, '')) +
                    octet_length(COALESCE(state, '')) +
                    octet_length(COALESCE(last_error, '')) +
                    octet_length(COALESCE(locked_by, ''))
                ), 0)
                FROM outbox_messages;
                """,
                ct)
            .ConfigureAwait(false);

        var httpArtifactBytes = await LoadFileStoreBytesAsync(fileStoreFactory, ct).ConfigureAwait(false);

        return new OpsStorageMetrics(
            assetMetadataBytes,
            httpArtifactBytes,
            inlineHttpBytes,
            eventJournalBytes);
    }

    private static async Task<long> LoadFileStoreBytesAsync(
        IDbContextFactory<FileStoreDbContext> fileStoreFactory,
        CancellationToken ct)
    {
        try
        {
            await using var fileStore = await fileStoreFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await fileStore.Blobs.AsNoTracking()
                .SumAsync(b => (long?)b.ContentLength, ct)
                .ConfigureAwait(false) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<long> ExecuteScalarLongAsync(
        ArgusDbContext db,
        string sql,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value switch
        {
            long l => l,
            int i => i,
            decimal d => (long)d,
            _ => 0,
        };
    }
}

internal sealed record OpsStorageMetrics(
    long AssetMetadataBytes,
    long HttpArtifactBytes,
    long InlineHttpBytes,
    long EventJournalBytes)
{
    public long TotalBytes => AssetMetadataBytes + HttpArtifactBytes + InlineHttpBytes + EventJournalBytes;
}
