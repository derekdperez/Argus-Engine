using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/assets",
                async (
                    [FromQuery] Guid? targetId,
                    [FromQuery] string? kind,
                    [FromQuery] string? status,
                    [FromQuery] int limit,
                    ArgusDbContext db,
                    CancellationToken ct) =>
                {
                    var query = db.Assets.AsNoTracking();

                    if (targetId.HasValue)
                        query = query.Where(a => a.TargetId == targetId.Value);
                    if (!string.IsNullOrWhiteSpace(kind))
                        query = query.Where(a => a.Kind == kind);
                    if (!string.IsNullOrWhiteSpace(status))
                        query = query.Where(a => a.LifecycleStatus == status);

                    var rows = await query
                        .OrderByDescending(a => a.DiscoveredAtUtc)
                        .Take(limit > 0 ? Math.Min(limit, 1000) : 100)
                        .Select(a => new AssetGridRowDto(
                            a.Id,
                            a.TargetId,
                            a.Kind,
                            a.Category,
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
}
