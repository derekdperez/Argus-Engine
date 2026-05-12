using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Amazon;
using Amazon.ECS;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using DescribeServicesRequest = Amazon.ECS.Model.DescribeServicesRequest;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

public static class AdminUsageEndpoints
{
    private const decimal EcsFreeTierWorkerHours = 2200m;

    public static IEndpointRouteBuilder MapAdminUsageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/admin/usage",
                async (ArgusDbContext db, IConfiguration configuration, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

                    List<CloudResourceUsageSample> samples;
                    try
                    {
                        samples = await db.CloudResourceUsageSamples.AsNoTracking()
                            .Where(s => s.SampledAtUtc >= monthStart.AddMonths(-1) && s.SampledAtUtc <= now)
                            .OrderBy(s => s.ResourceKind)
                            .ThenBy(s => s.ResourceId)
                            .ThenBy(s => s.SampledAtUtc)
                            .ToListAsync(ct)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        samples = [];
                    }

                    var cloud = BuildCloudUsage(samples, monthStart, now);
                    await AddLiveEcsFallbackRowsAsync(cloud, configuration, monthStart, now, ct).ConfigureAwait(false);
                    AddProcessEc2FallbackRow(cloud, monthStart, now);
                    var traffic = await LoadTrafficOrEmptyAsync(db, monthStart, ct).ConfigureAwait(false);
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
            .WithName("ListAdminUsage");
        return app;
    }

    private static async Task<TrafficStats> LoadTrafficOrEmptyAsync(ArgusDbContext db, DateTimeOffset monthStart, CancellationToken ct)
    {
        try
        {
            return await LoadTrafficAsync(db, monthStart, ct).ConfigureAwait(false);
        }
        catch
        {
            return new TrafficStats(0, 0, 0, 0, 0);
        }
    }

    private static async Task AddLiveEcsFallbackRowsAsync(
        List<CloudUsageResourceDto> cloud,
        IConfiguration configuration,
        DateTimeOffset monthStart,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = cloud
            .Where(r => r.ResourceKind == "ecs-worker")
            .Select(r => r.ResourceName)
            .ToHashSet(StringComparer.Ordinal);

        var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            return;

        var services = new[]
        {
            configuration["WORKER_SPIDER_SERVICE"] ?? "nightmare-worker-spider",
            configuration["WORKER_ENUM_SERVICE"] ?? "nightmare-worker-enum",
            configuration["WORKER_PORTSCAN_SERVICE"] ?? "nightmare-worker-portscan",
            configuration["WORKER_HIGHVALUE_SERVICE"] ?? "nightmare-worker-highvalue",
            configuration["WORKER_TECHID_SERVICE"] ?? "nightmare-worker-techid",
        };

        try
        {
            using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
            var response = await ecs.DescribeServicesAsync(
                    new DescribeServicesRequest
                    {
                        Cluster = configuration["ECS_CLUSTER"] ?? "nightmare-v2",
                        Services = services.Distinct(StringComparer.Ordinal).ToList(),
                    },
                    ct)
                .ConfigureAwait(false);

            foreach (var service in response.Services)
            {
                if (!existing.Add(service.ServiceName))
                    continue;

                var start = service.CreatedAt is { } createdAt && createdAt != default
                    ? new DateTimeOffset(createdAt, TimeSpan.Zero)
                    : monthStart;
                if (start < monthStart)
                    start = monthStart;

                var running = Math.Max(0, service.RunningCount.GetValueOrDefault());
                var hours = now > start ? running * DecimalHours(now - start) : 0m;
                cloud.Add(
                    new CloudUsageResourceDto(
                        "ecs-worker",
                        service.ServiceArn ?? service.ServiceName,
                        service.ServiceName,
                        running,
                        now,
                        RoundHours(hours)));
            }
        }
        catch
        {
            // Admin usage still reports persisted samples and local process uptime when AWS is unavailable.
        }
    }

    private static void AddProcessEc2FallbackRow(List<CloudUsageResourceDto> cloud, DateTimeOffset monthStart, DateTimeOffset now)
    {
        if (cloud.Any(r => r.ResourceKind == "ec2-server"))
            return;

        var start = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        if (start < monthStart)
            start = monthStart;

        cloud.Add(
            new CloudUsageResourceDto(
                "ec2-server",
                Environment.MachineName,
                Environment.MachineName,
                1,
                now,
                now > start ? RoundHours(DecimalHours(now - start)) : 0m));
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

    private static async Task<string?> ResolveAwsRegionAsync(IConfiguration configuration, CancellationToken ct)
    {
        var configured = configuration["AWS_REGION"]
            ?? configuration["AWS_DEFAULT_REGION"]
            ?? configuration["AWS:Region"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync("http://169.254.169.254/latest/dynamic/instance-identity/document", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("region", out var region) ? region.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<TrafficStats> LoadTrafficAsync(ArgusDbContext db, DateTimeOffset monthStart, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*) FILTER (WHERE COALESCE(completed_at_utc, created_at_utc) >= @month_start) AS month_requests,
                COALESCE(SUM(octet_length(COALESCE(request_headers_json, '')) + octet_length(COALESCE(request_body, '')))
                    FILTER (WHERE COALESCE(completed_at_utc, created_at_utc) >= @month_start), 0) AS month_request_bytes,
                COALESCE(SUM(octet_length(COALESCE(response_headers_json, '')) + COALESCE(response_content_length, octet_length(COALESCE(response_body, ''))))
                    FILTER (WHERE COALESCE(completed_at_utc, created_at_utc) >= @month_start), 0) AS month_response_bytes,
                COALESCE(SUM(octet_length(COALESCE(request_headers_json, '')) + octet_length(COALESCE(request_body, ''))), 0) AS all_request_bytes,
                COALESCE(SUM(octet_length(COALESCE(response_headers_json, '')) + COALESCE(response_content_length, octet_length(COALESCE(response_body, '')))), 0) AS all_response_bytes
            FROM http_request_queue;
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

