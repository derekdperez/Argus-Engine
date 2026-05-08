using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.Operations.Api;

internal static class OperationsSnapshotBuilder
{
    public const string RabbitHttpClientName = "ops-rabbit";

    private static readonly JsonSerializerOptions RabbitJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly (string Key, string ConsumerSubstring)[] WorkerConsumeMarkers =
    [
        (WorkerKeys.Gatekeeper, "Gatekeeper.Consumers.AssetDiscoveredConsumer"),
        (WorkerKeys.Spider, "SpiderAssetDiscoveredConsumer"),
        (WorkerKeys.Enumeration, "Workers.Enum.Consumers.SubdomainEnumerationRequestedConsumer"),
        (WorkerKeys.PortScan, "PortScanRequestedConsumer"),
        (WorkerKeys.HighValueRegex, "Workers.HighValue.Consumers.HighValueRegexConsumer"),
        (WorkerKeys.HighValuePaths, "Workers.HighValue.Consumers.HighValuePathGuessConsumer"),
        (WorkerKeys.TechnologyIdentification, "Workers.TechnologyIdentification.Consumers.TechnologyIdentificationConsumer"),
    ];

    public static async Task<OpsSnapshotDto> BuildAsync(
        ArgusDbContext db,
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var h1 = now.AddHours(-1);
        var h24 = now.AddHours(-24);
        var rabbitTask = LoadRabbitQueuesAsync(httpFactory, configuration, cancellationToken);

        var workers = await db.WorkerSwitches.AsNoTracking()
            .OrderBy(w => w.WorkerKey)
            .Select(w => new WorkerSwitchDto(w.WorkerKey, w.IsEnabled, w.UpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var activity = await WorkerActivityQuery.BuildSnapshotAsync(db, cancellationToken).ConfigureAwait(false);
        var assets = await LoadAssetSummaryAsync(db, h1, h24, cancellationToken).ConfigureAwait(false);
        var busTraffic = await LoadBusTrafficAsync(db, h1, h24, cancellationToken).ConfigureAwait(false);
        var (queues, rabbitOk) = await rabbitTask.ConfigureAwait(false);
        var rabbitByWorker = AggregateRabbitByWorker(queues);

        var stats = new List<WorkerDetailStatsDto>();
        foreach (var key in WorkerConsumeMarkers.Select(marker => marker.Key))
        {
            stats.Add(await BuildOneWorkerDetailAsync(db, key, h1, h24, rabbitByWorker, cancellationToken).ConfigureAwait(false));
        }

        return new OpsSnapshotDto(workers, activity, assets, busTraffic, stats, queues, rabbitOk);
    }

    public static async Task<(List<RabbitQueueBriefDto> Queues, bool Ok)> LoadRabbitQueuesAsync(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var baseUrl = configuration["RabbitMq:ManagementUrl"]?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
        {
            var host = configuration["RabbitMq:Host"]?.Trim();
            if (string.IsNullOrEmpty(host))
            {
                return ([], false);
            }

            baseUrl = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? host
                : $"http://{host}:15672";
        }

        var vhostSeg = Uri.EscapeDataString(string.IsNullOrWhiteSpace(configuration["RabbitMq:VirtualHost"]) ? "/" : configuration["RabbitMq:VirtualHost"]!);
        var url = $"{baseUrl.TrimEnd('/')}/api/queues/{vhostSeg}";
        var user = configuration["RabbitMq:Username"] ?? "guest";
        var pass = configuration["RabbitMq:Password"] ?? "guest";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}")));

            using var resp = await httpFactory.CreateClient(RabbitHttpClientName)
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return ([], false);
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize<List<RabbitMgmtQueueRow>>(json, RabbitJson) ?? [];
            var queues = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.Name) && !row.Name.StartsWith("amq.", StringComparison.OrdinalIgnoreCase))
                .Select(row => new RabbitQueueBriefDto(row.Name!, row.Messages, row.MessagesReady, row.MessagesUnacknowledged, row.Consumers, GuessWorkerFromQueueName(row.Name!)))
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (queues, true);
        }
        catch
        {
            return ([], false);
        }
    }

    private static async Task<AssetOpsSummaryDto> LoadAssetSummaryAsync(
        ArgusDbContext db,
        DateTimeOffset h1,
        DateTimeOffset h24,
        CancellationToken ct)
    {
        var assets = db.Assets.AsNoTracking();
        var topDomains = await assets
            .Join(db.Targets.AsNoTracking(), a => a.TargetId, t => t.Id, (_, t) => t.RootDomain)
            .GroupBy(d => d)
            .Select(g => new AssetCountByDomainDto(g.Key, g.LongCount()))
            .OrderByDescending(x => x.Count)
            .Take(25)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var discoveredBy = await assets
            .GroupBy(a => a.DiscoveredBy)
            .Select(g => new DiscoveredByCountDto(g.Key, g.LongCount()))
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new AssetOpsSummaryDto(
            await assets.LongCountAsync(ct).ConfigureAwait(false),
            await db.Targets.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.DiscoveredAtUtc >= h1, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.DiscoveredAtUtc >= h24, ct).ConfigureAwait(false),
            await assets.OrderByDescending(a => a.DiscoveredAtUtc).Select(a => (DateTimeOffset?)a.DiscoveredAtUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.LifecycleStatus == "Discovered", ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.LifecycleStatus == AssetLifecycleStatus.Queued, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => (a.LifecycleStatus == "Discovered" || a.LifecycleStatus == AssetLifecycleStatus.Queued) && (a.Kind == AssetKind.Url || a.Kind == AssetKind.ApiEndpoint || a.Kind == AssetKind.JavaScriptFile || a.Kind == AssetKind.MarkdownBody || a.Kind == AssetKind.Subdomain || a.Kind == AssetKind.Domain), ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.Subdomain, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.Domain, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.IpAddress, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.Url, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.Url && a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.Url || a.Kind == AssetKind.ApiEndpoint || a.Kind == AssetKind.JavaScriptFile || a.Kind == AssetKind.MarkdownBody, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => (a.Kind == AssetKind.Url || a.Kind == AssetKind.ApiEndpoint || a.Kind == AssetKind.JavaScriptFile || a.Kind == AssetKind.MarkdownBody) && a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.TypeDetailsJson != null && a.TypeDetailsJson != "", ct).ConfigureAwait(false),
            await assets.LongCountAsync(a => a.Kind == AssetKind.OpenPort, ct).ConfigureAwait(false),
            await db.HighValueFindings.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false),
            topDomains,
            discoveredBy);
    }

    private static async Task<BusTrafficSummaryDto> LoadBusTrafficAsync(ArgusDbContext db, DateTimeOffset h1, DateTimeOffset h24, CancellationToken ct) =>
        new(
            await db.BusJournal.AsNoTracking().LongCountAsync(e => e.Direction == "Publish" && e.OccurredAtUtc >= h1, ct).ConfigureAwait(false),
            await db.BusJournal.AsNoTracking().LongCountAsync(e => e.Direction == "Publish" && e.OccurredAtUtc >= h24, ct).ConfigureAwait(false),
            await db.BusJournal.AsNoTracking().LongCountAsync(e => e.Direction == "Consume" && e.OccurredAtUtc >= h1, ct).ConfigureAwait(false),
            await db.BusJournal.AsNoTracking().LongCountAsync(e => e.Direction == "Consume" && e.OccurredAtUtc >= h24, ct).ConfigureAwait(false));

    private static async Task<WorkerDetailStatsDto> BuildOneWorkerDetailAsync(
        ArgusDbContext db,
        string workerKey,
        DateTimeOffset h1,
        DateTimeOffset h24,
        Dictionary<string, RabbitAgg> rabbitByWorker,
        CancellationToken ct)
    {
        var marker = WorkerConsumeMarkers.First(m => m.Key == workerKey).ConsumerSubstring;
        var consumed = db.BusJournal.AsNoTracking().Where(e => e.Direction == "Consume" && e.ConsumerType != null && e.ConsumerType.Contains(marker));
        var (a1, a24) = await LoadAttributedAssetsAsync(db, workerKey, h1, h24, ct).ConfigureAwait(false);
        rabbitByWorker.TryGetValue(workerKey, out var rabbit);

        return new WorkerDetailStatsDto(
            workerKey,
            await consumed.LongCountAsync(e => e.OccurredAtUtc >= h1, ct).ConfigureAwait(false),
            await consumed.LongCountAsync(e => e.OccurredAtUtc >= h24, ct).ConfigureAwait(false),
            await consumed.OrderByDescending(e => e.Id).Select(e => (DateTimeOffset?)e.OccurredAtUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false),
            a1,
            a24,
            rabbit?.Ready ?? 0,
            rabbit?.Unacked ?? 0,
            rabbit?.Names ?? []);
    }

    private static async Task<(long H1, long H24)> LoadAttributedAssetsAsync(ArgusDbContext db, string workerKey, DateTimeOffset h1, DateTimeOffset h24, CancellationToken ct)
    {
        var assets = db.Assets.AsNoTracking();
        IQueryable<StoredAsset> attributed = workerKey switch
        {
            WorkerKeys.Gatekeeper => assets.Where(a => a.DiscoveredBy == "gatekeeper"),
            WorkerKeys.Spider => assets.Where(a => a.DiscoveredBy == "spider-worker"),
            WorkerKeys.Enumeration => assets.Where(a => EF.Functions.ILike(a.DiscoveredBy, "enum-worker:%")),
            _ => assets.Where(_ => false),
        };

        return (
            await attributed.LongCountAsync(a => a.DiscoveredAtUtc >= h1, ct).ConfigureAwait(false),
            await attributed.LongCountAsync(a => a.DiscoveredAtUtc >= h24, ct).ConfigureAwait(false));
    }

    private static Dictionary<string, RabbitAgg> AggregateRabbitByWorker(IReadOnlyList<RabbitQueueBriefDto> queues)
    {
        var result = new Dictionary<string, RabbitAgg>(StringComparer.Ordinal);
        foreach (var queue in queues)
        {
            if (queue.LikelyWorkerKey is not { } workerKey)
            {
                continue;
            }

            if (!result.TryGetValue(workerKey, out var aggregate))
            {
                aggregate = new RabbitAgg();
                result[workerKey] = aggregate;
            }

            aggregate.Ready += queue.MessagesReady;
            aggregate.Unacked += queue.MessagesUnacknowledged;
            aggregate.Names.Add(queue.Name);
        }

        return result;
    }

    private static string? GuessWorkerFromQueueName(string queueName)
    {
        var normalized = queueName.ToLowerInvariant();
        if (normalized.Contains("spider")) return WorkerKeys.Spider;
        if (normalized.Contains("gatekeeper")) return WorkerKeys.Gatekeeper;
        if (normalized.Contains("port-scan") || normalized.Contains("portscan")) return WorkerKeys.PortScan;
        if (normalized.Contains("enum")) return WorkerKeys.Enumeration;
        if (normalized.Contains("highvaluepath") || normalized.Contains("hvpath")) return WorkerKeys.HighValuePaths;
        if (normalized.Contains("technology") || normalized.Contains("techid")) return WorkerKeys.TechnologyIdentification;
        if (normalized.Contains("highvalue") || normalized.Contains("scannable")) return WorkerKeys.HighValueRegex;
        return null;
    }

    private sealed class RabbitAgg
    {
        public long Ready;
        public long Unacked;
        public List<string> Names { get; } = [];
    }

    private sealed class RabbitMgmtQueueRow
    {
        public string? Name { get; set; }
        public int Messages { get; set; }

        [JsonPropertyName("messages_ready")]
        public int MessagesReady { get; set; }

        [JsonPropertyName("messages_unacknowledged")]
        public int MessagesUnacknowledged { get; set; }

        public int Consumers { get; set; }
    }
}
