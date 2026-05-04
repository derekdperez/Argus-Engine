using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class EventTraceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/events/live",
                async (NightmareDbContext db, int? minutes, int? take, CancellationToken ct) =>
                {
                    var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 15, 1, 240));
                    var publishLimit = Math.Clamp(take ?? 250, 25, 1000);
                    var journalLimit = Math.Clamp(publishLimit * 8, publishLimit, 6000);
                    var since = DateTimeOffset.UtcNow - window;

                    var journalRows = await db.BusJournal.AsNoTracking()
                        .Where(e => e.OccurredAtUtc >= since)
                        .OrderByDescending(e => e.Id)
                        .Take(journalLimit)
                        .Select(e => new
                        {
                            e.Id,
                            e.Direction,
                            e.MessageType,
                            e.PayloadJson,
                            e.OccurredAtUtc,
                            e.ConsumerType,
                            e.HostName,
                        })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var parsedRows = journalRows
                        .Select(r => ParseJournalRow(
                            r.Id,
                            r.Direction,
                            r.MessageType,
                            r.PayloadJson,
                            r.OccurredAtUtc,
                            r.ConsumerType,
                            r.HostName))
                        .ToList();

                    var consumesByEventId = parsedRows
                        .Where(r => r.IsConsume && r.EventId is not null)
                        .ToLookup(r => r.EventId!.Value);

                    var publishes = parsedRows
                        .Where(r => r.IsPublish)
                        .OrderByDescending(r => r.OccurredAtUtc)
                        .ThenByDescending(r => r.JournalId)
                        .Take(publishLimit)
                        .ToList();

                    var followUpPublishes = parsedRows
                        .Where(r => r.IsPublish && r.CausationId is not null)
                        .ToList();

                    var rows = publishes
                        .Select(p =>
                        {
                            var consumers = p.EventId is null
                                ? []
                                : consumesByEventId[p.EventId.Value]
                                    .OrderBy(c => c.OccurredAtUtc)
                                    .ThenBy(c => c.JournalId)
                                    .Select(c => new EventConsumerTraceDto(
                                        c.JournalId,
                                        ConsumerLabel(c.ConsumerType),
                                        c.ConsumerType ?? "",
                                        c.HostName,
                                        c.OccurredAtUtc,
                                        Math.Max(0, (long)(c.OccurredAtUtc - p.OccurredAtUtc).TotalMilliseconds)))
                                    .ToList();

                            var followUps = p.EventId is null
                                ? []
                                : followUpPublishes
                                    .Where(f => f.JournalId != p.JournalId && f.CausationId == p.EventId)
                                    .OrderBy(f => f.OccurredAtUtc)
                                    .ThenBy(f => f.JournalId)
                                    .Take(8)
                                    .Select(f => new EventFollowUpTraceDto(
                                        f.JournalId,
                                        f.MessageType,
                                        f.Producer,
                                        f.HostName,
                                        f.OccurredAtUtc,
                                        f.PayloadPreview))
                                    .ToList();

                            return new EventTraceRowDto(
                                p.JournalId,
                                p.MessageType,
                                p.OccurredAtUtc,
                                p.EventOccurredAtUtc,
                                p.Producer,
                                p.HostName,
                                p.EventId,
                                p.CorrelationId,
                                p.CausationId,
                                p.PayloadPreview,
                                p.PayloadJson,
                                consumers.Count,
                                consumers,
                                followUps);
                        })
                        .ToList();

                    return Results.Ok(rows);
                })
            .WithName("LiveEventTrace");
    }

    private static ParsedJournalRow ParseJournalRow(
        long journalId,
        string direction,
        string messageType,
        string payloadJson,
        DateTimeOffset occurredAtUtc,
        string? consumerType,
        string hostName)
    {
        Guid? eventId = null;
        Guid? correlationId = null;
        Guid? causationId = null;
        DateTimeOffset? eventOccurredAtUtc = null;
        var producer = "";
        var preview = CompactPayload(payloadJson, out var parsedMetadata);

        if (parsedMetadata is not null)
        {
            eventId = parsedMetadata.EventId;
            correlationId = parsedMetadata.CorrelationId;
            causationId = parsedMetadata.CausationId;
            eventOccurredAtUtc = parsedMetadata.EventOccurredAtUtc;
            producer = parsedMetadata.Producer ?? "";
        }

        return new ParsedJournalRow(
            journalId,
            direction,
            messageType,
            payloadJson,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(consumerType) ? null : consumerType,
            hostName,
            eventId,
            correlationId,
            causationId,
            eventOccurredAtUtc,
            string.IsNullOrWhiteSpace(producer) ? "unknown" : producer,
            preview);
    }

    private static string CompactPayload(string payloadJson, out PayloadMetadata? metadata)
    {
        metadata = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Truncate(payloadJson, 360);

            metadata = new PayloadMetadata(
                TryGetGuid(root, "EventId"),
                TryGetGuid(root, "CorrelationId"),
                TryGetGuid(root, "CausationId"),
                TryGetDateTimeOffset(root, "OccurredAtUtc"),
                TryGetString(root, "Producer"));

            var parts = new List<string>(capacity: 8);
            foreach (var prop in root.EnumerateObject())
            {
                if (IsEnvelopeProperty(prop.Name))
                    continue;

                parts.Add($"{prop.Name}={FormatPreviewValue(prop.Value)}");
                if (parts.Count >= 8)
                    break;
            }

            return Truncate(string.Join("; ", parts), 360);
        }
        catch (JsonException)
        {
            return Truncate(payloadJson, 360);
        }
    }

    private static bool IsEnvelopeProperty(string name) =>
        name.Equals("EventId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CorrelationId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CausationId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("OccurredAtUtc", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Producer", StringComparison.OrdinalIgnoreCase);

    private static string FormatPreviewValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => Truncate(value.GetString() ?? "", 96),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"[{value.GetArrayLength()}]",
            JsonValueKind.Object => "{...}",
            _ => Truncate(value.GetRawText(), 96),
        };

    private static string ConsumerLabel(string? consumerType)
    {
        if (string.IsNullOrWhiteSpace(consumerType))
            return "unknown consumer";

        var kind = WorkerConsumerKindResolver.KindFromConsumerType(consumerType);
        var shortName = ShortTypeName(consumerType);
        return string.IsNullOrWhiteSpace(kind) || string.Equals(kind, shortName, StringComparison.OrdinalIgnoreCase)
            ? shortName
            : $"{kind} ({shortName})";
    }

    private static string ShortTypeName(string typeName)
    {
        var trimmed = typeName.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma >= 0)
            trimmed = trimmed[..comma];
        var plus = trimmed.LastIndexOf('+');
        var dot = trimmed.LastIndexOf('.');
        var idx = Math.Max(plus, dot);
        return idx >= 0 && idx + 1 < trimmed.Length ? trimmed[(idx + 1)..] : trimmed;
    }

    private static Guid? TryGetGuid(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var parsed))
            return parsed == Guid.Empty ? null : parsed;

        return prop.TryGetGuid(out parsed) && parsed != Guid.Empty ? parsed : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(prop.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals(name) || prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private sealed record PayloadMetadata(
        Guid? EventId,
        Guid? CorrelationId,
        Guid? CausationId,
        DateTimeOffset? EventOccurredAtUtc,
        string? Producer);

    private sealed record ParsedJournalRow(
        long JournalId,
        string Direction,
        string MessageType,
        string PayloadJson,
        DateTimeOffset OccurredAtUtc,
        string? ConsumerType,
        string HostName,
        Guid? EventId,
        Guid? CorrelationId,
        Guid? CausationId,
        DateTimeOffset? EventOccurredAtUtc,
        string Producer,
        string PayloadPreview)
    {
        public bool IsPublish => Direction.Equals("Publish", StringComparison.OrdinalIgnoreCase);
        public bool IsConsume => Direction.Equals("Consume", StringComparison.OrdinalIgnoreCase);
    }
}
