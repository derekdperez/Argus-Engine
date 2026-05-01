using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class AdminUsageEndpoints
{
    private const decimal EcsFreeTierWorkerHours = 2200m;

    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/admin/usage",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

                    var samples = await db.CloudResourceUsageSamples.AsNoTracking()
                        .Where(s => s.SampledAtUtc >= monthStart.AddMonths(-1) && s.SampledAtUtc <= now)
                        .OrderBy(s => s.ResourceKind)
                        .ThenBy(s => s.ResourceId)
                        .ThenBy(s => s.SampledAtUtc)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var cloud = BuildCloudUsage(samples, monthStart, now);
                    var traffic = await LoadTrafficAsync(db, monthStart, ct).ConfigureAwait(false);
                    var ecsHours = cloud.Where(r => r.ResourceKind == "ecs-worker").Sum(r => r.HoursMonthToDate);
                    var ec2Hours = cloud.Where(r => r.ResourceKind == "ec2-server").Sum(r => r.HoursMonthToDate);

                    return Results.Ok(
                        new AdminUsageSnapshotDto(
                            now,
                            monthStart,
                            RoundHours(ecsHours),
                            EcsFreeTierWorkerHours,
                            RoundHours(Math.Max(0m, EcsFreeTierWorkerHours - ecsHours)),
                            EcsFreeTierWorkerHours <= 0 ? 0 : Math.Round(ecsHours / EcsFreeTierWorkerHours * 100m, 2),
                            RoundHours(ec2Hours),
                            traffic.MonthRequests,
                            traffic.MonthRequestBytes + traffic.MonthResponseBytes,
                            traffic.MonthRequestBytes,
                            traffic.MonthResponseBytes,
                            traffic.AllTimeRequestBytes + traffic.AllTimeResponseBytes,
                            samples.Count == 0 ? null : samples.Max(s => (DateTimeOffset?)s.SampledAtUtc),
                            cloud));
                })
            .WithName("AdminUsage");
    }

    private static List<CloudUsageResourceDto> BuildCloudUsage(
        IReadOnlyList<CloudResourceUsageSample> samples,
        DateTimeOffset monthStart,
        DateTimeOffset now)
    {
        var rows = new List<CloudUsageResourceDto>();
        foreach (var group in samples.GroupBy(s => new { s.ResourceKind, s.ResourceId }))
        {
            var ordered = group.OrderBy(s => s.SampledAtUtc).ToList();
            var last = ordered.LastOrDefault();
            var firstInMonth = ordered.FirstOrDefault(s => s.SampledAtUtc >= monthStart);
            var previous = ordered.LastOrDefault(s => s.SampledAtUtc < monthStart);

            var cursor = monthStart;
            var running = previous?.RunningCount ?? 0;
            var hours = 0m;

            foreach (var sample in ordered.Where(s => s.SampledAtUtc >= monthStart))
            {
                if (sample.SampledAtUtc > cursor)
                    hours += running * DecimalHours(sample.SampledAtUtc - cursor);

                running = Math.Max(0, sample.RunningCount);
                cursor = sample.SampledAtUtc;
            }

            if (now > cursor)
                hours += running * DecimalHours(now - cursor);

            if (group.Key.ResourceKind == "ec2-server")
                hours = TryCalculateEc2HoursFromLaunchMetadata(last, monthStart, now) ?? hours;

            rows.Add(
                new CloudUsageResourceDto(
                    group.Key.ResourceKind,
                    group.Key.ResourceId,
                    last?.ResourceName ?? firstInMonth?.ResourceName ?? group.Key.ResourceId,
                    last?.RunningCount ?? 0,
                    last?.SampledAtUtc,
                    RoundHours(hours)));
        }

        return rows
            .OrderBy(r => r.ResourceKind, StringComparer.Ordinal)
            .ThenBy(r => r.ResourceName, StringComparer.Ordinal)
            .ToList();
    }

    private static decimal? TryCalculateEc2HoursFromLaunchMetadata(
        CloudResourceUsageSample? sample,
        DateTimeOffset monthStart,
        DateTimeOffset now)
    {
        if (sample?.MetadataJson is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(sample.MetadataJson);
            if (!doc.RootElement.TryGetProperty("launchTime", out var launchProp))
                return null;

            if (!DateTimeOffset.TryParse(launchProp.GetString(), out var launchTime))
                return null;

            var start = launchTime > monthStart ? launchTime : monthStart;
            return now > start ? DecimalHours(now - start) : 0m;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal DecimalHours(TimeSpan span) => (decimal)span.TotalSeconds / 3600m;

    private static decimal RoundHours(decimal value) => Math.Round(value, 2);

    private static async Task<TrafficStats> LoadTrafficAsync(NightmareDbContext db, DateTimeOffset monthStart, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*) FILTER (WHERE completed_at_utc >= @month_start) AS month_requests,
                COALESCE(SUM(octet_length(COALESCE(request_headers_json, '')) + octet_length(COALESCE(request_body, '')))
                    FILTER (WHERE completed_at_utc >= @month_start), 0) AS month_request_bytes,
                COALESCE(SUM(octet_length(COALESCE(response_headers_json, '')) + COALESCE(response_content_length, octet_length(COALESCE(response_body, ''))))
                    FILTER (WHERE completed_at_utc >= @month_start), 0) AS month_response_bytes,
                COALESCE(SUM(octet_length(COALESCE(request_headers_json, '')) + octet_length(COALESCE(request_body, ''))), 0) AS all_request_bytes,
                COALESCE(SUM(octet_length(COALESCE(response_headers_json, '')) + COALESCE(response_content_length, octet_length(COALESCE(response_body, '')))), 0) AS all_response_bytes
            FROM http_request_queue
            WHERE completed_at_utc IS NOT NULL;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "month_start";
        parameter.Value = monthStart;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return new TrafficStats(0, 0, 0, 0, 0);

        return new TrafficStats(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private sealed record TrafficStats(
        long MonthRequests,
        long MonthRequestBytes,
        long MonthResponseBytes,
        long AllTimeRequestBytes,
        long AllTimeResponseBytes);
}
