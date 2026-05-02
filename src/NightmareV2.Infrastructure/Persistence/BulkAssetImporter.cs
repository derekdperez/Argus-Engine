using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace NightmareV2.Infrastructure.Persistence;

public class AssetRecord
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = string.Empty;
}

public class BulkAssetImporter
{
    private readonly string _connectionString;

    public BulkAssetImporter(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task BulkInsertAssetsAsync(List<AssetRecord> assets, CancellationToken token)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(token);

        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY discovered_assets (id, url, method, metadata, created_at) FROM STDIN (FORMAT BINARY)",
            token
        );

        foreach (var asset in assets)
        {
            await writer.StartRowAsync(token);
            await writer.WriteAsync(asset.Id, NpgsqlDbType.Uuid, token);
            await writer.WriteAsync(asset.Url, NpgsqlDbType.Text, token);
            await writer.WriteAsync(asset.Method, NpgsqlDbType.Text, token);
            await writer.WriteAsync(asset.MetadataJson, NpgsqlDbType.Jsonb, token);
            await writer.WriteAsync(DateTime.UtcNow, NpgsqlDbType.TimestampTz, token);
        }

        await writer.CompleteAsync(token);
    }
}
