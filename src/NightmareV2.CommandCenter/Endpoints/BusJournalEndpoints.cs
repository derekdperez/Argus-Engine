using Microsoft.EntityFrameworkCore;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class BusJournalEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/bus/live",
                async (NightmareDbContext db, int? minutes, int? take, CancellationToken ct) =>
                {
                    var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 3, 1, 60));
                    var limit = Math.Clamp(take ?? 150, 1, 500);
                    var since = DateTimeOffset.UtcNow - window;
                    var rows = await db.BusJournal.AsNoTracking()
                        .Where(e => e.Direction == "Publish" && e.OccurredAtUtc >= since)
                        .OrderByDescending(e => e.OccurredAtUtc)
                        .Take(limit)
                        .Select(e => new BusJournalRowDto(e.Id, e.Direction, e.MessageType, e.PayloadJson, e.OccurredAtUtc, e.ConsumerType, e.HostName))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("BusLive");

        app.MapGet(
                "/api/bus/history",
                async (NightmareDbContext db, int? take, CancellationToken ct) =>
                {
                    var limit = Math.Clamp(take ?? 400, 1, 2000);
                    var rows = await db.BusJournal.AsNoTracking()
                        .OrderByDescending(e => e.Id)
                        .Take(limit)
                        .Select(e => new BusJournalRowDto(e.Id, e.Direction, e.MessageType, e.PayloadJson, e.OccurredAtUtc, e.ConsumerType, e.HostName))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("BusHistory");
    }
}
