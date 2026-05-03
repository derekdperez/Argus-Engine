using Microsoft.EntityFrameworkCore;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/assets",
                async (NightmareDbContext db, Guid? targetId, int? take, string? tag, CancellationToken ct) =>
                {
                    var q = db.Assets.AsNoTracking()
                        .Where(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
                        .OrderByDescending(a => a.DiscoveredAtUtc)
                        .AsQueryable();
                    if (targetId is { } tid)
                        q = q.Where(a => a.TargetId == tid);
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        var tagSlug = tag.Trim();
                        q = q.Where(a => db.AssetTags.Any(at => at.AssetId == a.Id && db.Tags.Any(t => t.Id == at.TagId && t.Slug == tagSlug)));
                    }

                    if (take is > 0)
                        q = q.Take(Math.Clamp(take.Value, 1, 1_000_000));

                    var rows = await q
                        .Select(a => new AssetGridRowDto(
                            a.Id,
                            a.TargetId,
                            a.Kind.ToString(),
                            a.Category.ToString(),
                            a.CanonicalKey,
                            a.RawValue,
                            a.DisplayName,
                            a.Depth,
                            a.DiscoveredBy,
                            a.DiscoveryContext,
                            a.DiscoveredAtUtc,
                            a.LastSeenAtUtc,
                            a.Confidence,
                            a.LifecycleStatus,
                            a.TypeDetailsJson,
                            a.FinalUrl,
                            a.RedirectCount,
                            a.RedirectChainJson))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("ListAssets");

        return app;
    }

    public static void Map(WebApplication app) => app.MapAssetEndpoints();
}
