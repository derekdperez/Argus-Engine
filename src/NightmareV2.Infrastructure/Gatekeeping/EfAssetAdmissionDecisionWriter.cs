using System.Data;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using NightmareV2.Application.Gatekeeping;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Observability;

namespace NightmareV2.Infrastructure.Gatekeeping;

public sealed class EfAssetAdmissionDecisionWriter(NightmareDbContext db) : IAssetAdmissionDecisionWriter
{
    public async Task WriteAsync(AssetAdmissionDecisionInput input, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO asset_admission_decisions (
                id,
                target_id,
                asset_id,
                raw_value,
                canonical_key,
                asset_kind,
                decision,
                reason_code,
                reason_detail,
                discovered_by,
                discovery_context,
                depth,
                global_max_depth,
                correlation_id,
                causation_id,
                event_id,
                occurred_at_utc)
            VALUES (
                @id,
                @target_id,
                @asset_id,
                @raw_value,
                @canonical_key,
                @asset_kind,
                @decision,
                @reason_code,
                @reason_detail,
                @discovered_by,
                @discovery_context,
                @depth,
                @global_max_depth,
                @correlation_id,
                @causation_id,
                @event_id,
                @occurred_at_utc);
            """;

        command.Parameters.Add(new NpgsqlParameter("id", Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter("target_id", input.TargetId));
        command.Parameters.Add(new NpgsqlParameter("asset_id", DbValue(input.AssetId)));
        command.Parameters.Add(new NpgsqlParameter("raw_value", Truncate(input.RawValue, 4096)));
        command.Parameters.Add(new NpgsqlParameter("canonical_key", DbValue(TruncateNullable(input.CanonicalKey, 2048))));
        command.Parameters.Add(new NpgsqlParameter("asset_kind", Truncate(input.AssetKind, 64)));
        command.Parameters.Add(new NpgsqlParameter("decision", Truncate(input.Decision, 64)));
        command.Parameters.Add(new NpgsqlParameter("reason_code", Truncate(input.ReasonCode, 128)));
        command.Parameters.Add(new NpgsqlParameter("reason_detail", DbValue(TruncateNullable(input.ReasonDetail, 2048))));
        command.Parameters.Add(new NpgsqlParameter("discovered_by", Truncate(input.DiscoveredBy, 128)));
        command.Parameters.Add(new NpgsqlParameter("discovery_context", DbValue(TruncateNullable(input.DiscoveryContext, 1024))));
        command.Parameters.Add(new NpgsqlParameter("depth", input.Depth));
        command.Parameters.Add(new NpgsqlParameter("global_max_depth", input.GlobalMaxDepth));
        command.Parameters.Add(new NpgsqlParameter("correlation_id", input.CorrelationId));
        command.Parameters.Add(new NpgsqlParameter("causation_id", DbValue(input.CausationId)));
        command.Parameters.Add(new NpgsqlParameter("event_id", DbValue(input.EventId)));
        command.Parameters.Add(new NpgsqlParameter("occurred_at_utc", DateTimeOffset.UtcNow));

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        ArgusMeters.AssetAdmissionDecisions.Add(
            1,
            new KeyValuePair<string, object?>("decision", input.Decision),
            new KeyValuePair<string, object?>("reason_code", input.ReasonCode),
            new KeyValuePair<string, object?>("asset_kind", input.AssetKind));

        if (string.Equals(input.Decision, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            ArgusMeters.AssetsDiscovered.Add(
                1,
                new KeyValuePair<string, object?>("asset_kind", input.AssetKind),
                new KeyValuePair<string, object?>("source", input.DiscoveredBy));
        }
    }

    private static object DbValue<T>(T? value) => value is null ? DBNull.Value : value;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? TruncateNullable(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : Truncate(value, max);
}
