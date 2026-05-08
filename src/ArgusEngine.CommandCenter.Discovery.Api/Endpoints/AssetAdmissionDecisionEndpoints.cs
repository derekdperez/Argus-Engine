using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using System.Data.Common;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using Npgsql;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class AssetAdmissionDecisionEndpoints
{
    public static IEndpointRouteBuilder MapAssetAdmissionDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/asset-admission-decisions", QueryAsync);
        app.MapGet("/api/targets/{targetId:guid}/asset-admission-decisions", QueryForTargetAsync);

        return app;
    }

    private static Task<IResult> QueryForTargetAsync(
        Guid targetId,
        IConfiguration configuration,
        [FromQuery] string? decision,
        [FromQuery] string? reasonCode,
        [FromQuery] string? assetKind,
        [FromQuery] string? canonicalKey,
        [FromQuery] string? rawContains,
        [FromQuery] string? discoveredBy,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken ct) =>
        QueryCoreAsync(
            configuration,
            targetId,
            decision,
            reasonCode,
            assetKind,
            canonicalKey,
            rawContains,
            discoveredBy,
            fromUtc,
            toUtc,
            skip,
            take,
            ct);

    private static Task<IResult> QueryAsync(
        IConfiguration configuration,
        [FromQuery] Guid? targetId,
        [FromQuery] string? decision,
        [FromQuery] string? reasonCode,
        [FromQuery] string? assetKind,
        [FromQuery] string? canonicalKey,
        [FromQuery] string? rawContains,
        [FromQuery] string? discoveredBy,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken ct) =>
        QueryCoreAsync(
            configuration,
            targetId,
            decision,
            reasonCode,
            assetKind,
            canonicalKey,
            rawContains,
            discoveredBy,
            fromUtc,
            toUtc,
            skip,
            take,
            ct);

    private static async Task<IResult> QueryCoreAsync(
        IConfiguration configuration,
        Guid? targetId,
        string? decision,
        string? reasonCode,
        string? assetKind,
        string? canonicalKey,
        string? rawContains,
        string? discoveredBy,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? skip,
        int? take,
        CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            return Results.Problem("Postgres connection string is not configured.");

        var limit = Math.Clamp(take ?? 200, 1, 1000);
        var offset = Math.Max(skip ?? 0, 0);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        var filters = new List<string>();
        AddFilter(command, filters, "target_id", targetId);
        AddFilter(command, filters, "decision", decision);
        AddFilter(command, filters, "reason_code", reasonCode);
        AddFilter(command, filters, "asset_kind", assetKind);
        AddFilter(command, filters, "canonical_key", canonicalKey);
        AddFilter(command, filters, "discovered_by", discoveredBy);

        if (!string.IsNullOrWhiteSpace(rawContains))
        {
            var parameterName = AddParameter(command, $"%{rawContains.Trim()}%");
            filters.Add($"(raw_value ILIKE {parameterName} OR canonical_key ILIKE {parameterName})");
        }

        if (fromUtc is not null)
        {
            var parameterName = AddParameter(command, fromUtc);
            filters.Add($"occurred_at_utc >= {parameterName}");
        }

        if (toUtc is not null)
        {
            var parameterName = AddParameter(command, toUtc);
            filters.Add($"occurred_at_utc <= {parameterName}");
        }

        var where = filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters);
        command.CommandText = $"""
            SELECT
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
                occurred_at_utc
            FROM asset_admission_decisions
            {where}
            ORDER BY occurred_at_utc DESC
            OFFSET {offset}
            LIMIT {limit};
            """;

        var rows = new List<AssetAdmissionDecisionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(ReadDto(reader));

        return Results.Ok(rows);
    }

    private static void AddFilter<T>(NpgsqlCommand command, List<string> filters, string columnName, T? value)
    {
        if (value is null)
            return;

        if (value is string text && string.IsNullOrWhiteSpace(text))
            return;

        var parameterName = AddParameter(command, value is string s ? s.Trim() : value);
        filters.Add($"{columnName} = {parameterName}");
    }

    private static string AddParameter(NpgsqlCommand command, object? value)
    {
        var name = $"@p{command.Parameters.Count}";
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return name;
    }

    private static AssetAdmissionDecisionDto ReadDto(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetGuid(13),
            reader.IsDBNull(14) ? null : reader.GetGuid(14),
            reader.IsDBNull(15) ? null : reader.GetGuid(15),
            reader.GetFieldValue<DateTimeOffset>(16));

    public sealed record AssetAdmissionDecisionDto(
        Guid Id,
        Guid TargetId,
        Guid? AssetId,
        string RawValue,
        string? CanonicalKey,
        string AssetKind,
        string Decision,
        string ReasonCode,
        string? ReasonDetail,
        string DiscoveredBy,
        string? DiscoveryContext,
        int Depth,
        int GlobalMaxDepth,
        Guid CorrelationId,
        Guid? CausationId,
        Guid? EventId,
        DateTimeOffset OccurredAtUtc);
}




