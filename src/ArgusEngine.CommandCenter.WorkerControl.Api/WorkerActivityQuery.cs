using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.WorkerControl.Api;

internal static class WorkerActivityQuery
{
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    private static readonly TimeSpan HotWindow = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);

    private static readonly string[] RequiredWorkerKeys =
    [
        WorkerKeys.Gatekeeper,
        WorkerKeys.Spider,
        WorkerKeys.Enumeration,
        WorkerKeys.PortScan,
        WorkerKeys.HighValueRegex,
        WorkerKeys.HighValuePaths,
        WorkerKeys.TechnologyIdentification,
    ];

    public static async Task<WorkerActivitySnapshotDto> BuildSnapshotAsync(ArgusDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - Lookback;
        var heartbeats = await db.WorkerHeartbeats.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var rows = await db.BusJournal.AsNoTracking()
            .Where(e => e.Direction == "Consume" && e.ConsumerType != null && e.OccurredAtUtc >= since)
            .OrderByDescending(e => e.Id)
            .Take(15_000)
            .Select(e => new { e.HostName, e.ConsumerType, e.MessageType, e.PayloadJson, e.OccurredAtUtc, e.Status, e.DurationMs, e.Error, e.MessageId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestByKey = new Dictionary<(string Host, string Consumer), JournalEntryDetail>();
        foreach (var row in rows)
        {
            var host = string.IsNullOrWhiteSpace(row.HostName) ? "(no host)" : row.HostName.Trim();
            latestByKey.TryAdd(
                (host, row.ConsumerType!),
                new JournalEntryDetail(row.MessageType, row.PayloadJson, row.OccurredAtUtc, row.Status, row.DurationMs, row.Error, row.MessageId));
        }

        var toggles = await db.WorkerSwitches.AsNoTracking()
            .ToDictionaryAsync(w => w.WorkerKey, w => w.IsEnabled, ct)
            .ConfigureAwait(false);
        foreach (var key in RequiredWorkerKeys)
        {
            toggles.TryAdd(key, true);
        }

        var instances = new List<WorkerInstanceActivityDto>();
        foreach (var ((host, consumer), detail) in latestByKey)
        {
            var kind = WorkerConsumerKindResolver.KindFromConsumerType(consumer) ?? "Other";
            var heartbeat = heartbeats.FirstOrDefault(h => h.HostName == host);
            var isAlive = heartbeat is not null && now - heartbeat.LastHeartbeatUtc < TimeSpan.FromMinutes(2);
            instances.Add(
                new WorkerInstanceActivityDto(
                    host,
                    kind,
                    ShortConsumerName(consumer),
                    kind == "Other" ? null : toggles.GetValueOrDefault(kind, true),
                    detail.At,
                    detail.MessageType,
                    TruncatePreview(detail.Payload),
                    ActivityLabel(detail.At, now, detail.Status, isAlive),
                    detail.Status,
                    detail.DurationMs,
                    detail.Error,
                    detail.MessageId));
        }

        foreach (var heartbeat in heartbeats)
        {
            if (instances.Any(i => i.HostName == heartbeat.HostName))
            {
                continue;
            }

            if (now - heartbeat.LastHeartbeatUtc >= TimeSpan.FromMinutes(2))
            {
                continue;
            }

            instances.Add(
                new WorkerInstanceActivityDto(
                    heartbeat.HostName,
                    heartbeat.WorkerKey,
                    "Idle",
                    toggles.GetValueOrDefault(heartbeat.WorkerKey, true),
                    heartbeat.LastHeartbeatUtc,
                    "-",
                    "-",
                    "Idle (Alive)",
                    "Idle",
                    null,
                    null,
                    null));
        }

        instances.Sort(
            (a, b) =>
            {
                var result = string.Compare(a.WorkerKind, b.WorkerKind, StringComparison.Ordinal);
                if (result != 0) return result;
                result = string.Compare(a.HostName, b.HostName, StringComparison.Ordinal);
                return result != 0 ? result : string.Compare(a.ConsumerShortName, b.ConsumerShortName, StringComparison.Ordinal);
            });

        var summaries = toggles.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(
                key =>
                {
                    var matching = instances.Where(i => i.WorkerKind == key).ToList();
                    var last = matching.Count == 0 ? (DateTimeOffset?)null : matching.Max(i => i.LastCompletedAtUtc);
                    return new WorkerKindSummaryDto(
                        key,
                        toggles[key],
                        matching.Count,
                        last,
                        last is null ? "No journal data (24h)" : ActivityLabel(last.Value, now, "Completed", matching.Any(i => i.Status == "Idle" || i.Status == "Started")));
                })
            .ToList();

        return new WorkerActivitySnapshotDto(summaries, instances);
    }

    private static string ActivityLabel(DateTimeOffset lastAt, DateTimeOffset now, string status, bool isAlive)
    {
        if (status == "Started") return "Processing...";
        if (status == "Failed") return "Last failed";

        var ago = now - lastAt;
        if (ago <= HotWindow) return "Hot (just finished)";
        if (ago <= RecentWindow) return "Recently active";
        return isAlive ? "Idle (Alive)" : "Stale / likely offline";
    }

    private static string ShortConsumerName(string fullName)
    {
        var i = fullName.LastIndexOf('.');
        return i >= 0 && i < fullName.Length - 1 ? fullName[(i + 1)..] : fullName;
    }

    private static string TruncatePreview(string json)
    {
        var value = json.ReplaceLineEndings(" ").Trim();
        const int max = 140;
        return value.Length <= max ? value : value[..max] + "...";
    }

    private sealed record JournalEntryDetail(
        string MessageType,
        string Payload,
        DateTimeOffset At,
        string Status,
        double? DurationMs,
        string? Error,
        Guid? MessageId);
}
