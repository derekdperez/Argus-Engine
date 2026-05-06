using ArgusEngine.CommandCenter.Models;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter;

internal static class WorkerActivityQuery
{
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    private static readonly TimeSpan HotWindow = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleWindow = TimeSpan.FromHours(1);
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

        var heartbeats = await db.WorkerHeartbeats.AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = await db.BusJournal.AsNoTracking()
            .Where(e => e.Direction == "Consume" && e.ConsumerType != null && e.OccurredAtUtc >= since)
            .OrderByDescending(e => e.Id)
            .Take(15_000)
            .Select(e => new { e.HostName, e.ConsumerType, e.MessageType, e.PayloadJson, e.OccurredAtUtc, e.Status, e.DurationMs, e.Error, e.MessageId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Group by Host, Consumer, and MessageId to find the latest status of each message
        var latestByKey = new Dictionary<(string Host, string Consumer), List<JournalEntryDetail>>();
        foreach (var r in rows)
        {
            var host = string.IsNullOrWhiteSpace(r.HostName) ? "(no host)" : r.HostName.Trim();
            var key = (host, r.ConsumerType!);
            if (!latestByKey.TryGetValue(key, out var list))
            {
                list = new List<JournalEntryDetail>();
                latestByKey[key] = list;
            }
            
            // Only keep the latest record for each MessageId if present, otherwise just the latest record overall
            if (r.MessageId.HasValue)
            {
                if (list.Any(x => x.MessageId == r.MessageId)) continue;
            }
            else if (list.Count > 0) continue;

            list.Add(new JournalEntryDetail(r.MessageType, r.PayloadJson, r.OccurredAtUtc, r.Status, r.DurationMs, r.Error, r.MessageId));
        }

        var toggles = await db.WorkerSwitches.AsNoTracking()
            .ToDictionaryAsync(w => w.WorkerKey, w => w.IsEnabled, ct)
            .ConfigureAwait(false);
        foreach (var key in RequiredWorkerKeys)
            toggles.TryAdd(key, true);

        var instances = new List<WorkerInstanceActivityDto>();
        foreach (var kv in latestByKey)
        {
            var (host, consumer) = kv.Key;
            var details = kv.Value.OrderByDescending(d => d.At).FirstOrDefault();
            if (details == null) continue;

            var kind = WorkerConsumerKindResolver.KindFromConsumerType(consumer) ?? "Other";
            bool? toggle = kind == "Other"
                ? null
                : (toggles.TryGetValue(kind, out var en) ? en : true);

            var heartbeat = heartbeats.FirstOrDefault(h => h.HostName == host);
            var isAlive = heartbeat != null && (now - heartbeat.LastHeartbeatUtc) < TimeSpan.FromMinutes(2);

            instances.Add(
                new WorkerInstanceActivityDto(
                    host,
                    kind,
                    ShortConsumerName(consumer),
                    toggle,
                    details.At,
                    details.MessageType,
                    TruncatePreview(details.Payload),
                    ActivityLabel(details.At, now, details.Status, isAlive),
                    details.Status,
                    details.DurationMs,
                    details.Error,
                    details.MessageId));
        }

        // Add instances that haven't consumed anything but have heartbeats
        foreach (var h in heartbeats)
        {
            if (instances.Any(i => i.HostName == h.HostName)) continue;

            instances.Add(new WorkerInstanceActivityDto(
                h.HostName,
                h.WorkerKey,
                "Idle",
                toggles.GetValueOrDefault(h.WorkerKey, true),
                h.LastHeartbeatUtc,
                "-",
                "-",
                "Idle (Alive)",
                "Idle",
                null,
                null,
                null));
        }

        instances.Sort(CompareInstances);

        var summaries = new List<WorkerKindSummaryDto>();
        foreach (var key in toggles.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var matching = instances.Where(i => i.WorkerKind == key).ToList();
            var last = matching.Count == 0 ? (DateTimeOffset?)null : matching.Max(i => i.LastCompletedAtUtc);
            var label = last is { } t ? ActivityLabel(t, now, "Completed", matching.Any(i => i.Status == "Idle" || i.Status == "Started")) : "No journal data (24h)";
            summaries.Add(
                new WorkerKindSummaryDto(
                    key,
                    toggles[key],
                    matching.Count,
                    last,
                    label));
        }

        return new WorkerActivitySnapshotDto(summaries, instances);
    }

    private record JournalEntryDetail(string MessageType, string Payload, DateTimeOffset At, string Status, double? DurationMs, string? Error, Guid? MessageId);

    private static string ActivityLabel(DateTimeOffset lastAt, DateTimeOffset now, string status, bool isAlive)
    {
        if (status == "Started") return "Processing...";
        if (status == "Failed") return "Last failed";
        
        var ago = now - lastAt;
        if (ago <= HotWindow)
            return "Hot (just finished)";
        if (ago <= RecentWindow)
            return "Recently active";
        if (isAlive)
            return "Idle (Alive)";
        return "Stale / likely offline";
    }

    private static int CompareInstances(WorkerInstanceActivityDto a, WorkerInstanceActivityDto b)
    {
        var c = string.Compare(a.WorkerKind, b.WorkerKind, StringComparison.Ordinal);
        if (c != 0)
            return c;
        c = string.Compare(a.HostName, b.HostName, StringComparison.Ordinal);
        if (c != 0)
            return c;
        return string.Compare(a.ConsumerShortName, b.ConsumerShortName, StringComparison.Ordinal);
    }

    private static string ActivityLabel(DateTimeOffset lastCompleted, DateTimeOffset now)
    {
        var ago = now - lastCompleted;
        if (ago <= HotWindow)
            return "Hot (just finished a message)";
        if (ago <= RecentWindow)
            return "Recently active";
        if (ago <= IdleWindow)
            return "Idle";
        return "Stale / likely offline";
    }

    private static string ShortConsumerName(string fullName)
    {
        var i = fullName.LastIndexOf('.');
        return i >= 0 && i < fullName.Length - 1 ? fullName[(i + 1)..] : fullName;
    }

    private static string TruncatePreview(string json)
    {
        var s = json.ReplaceLineEndings(" ").Trim();
        const int max = 140;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
