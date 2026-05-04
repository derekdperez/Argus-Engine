using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/ops/overview",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var totalTargets = await db.Targets.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
                    var totalAssetsConfirmed = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct)
                        .ConfigureAwait(false);
                    var totalUrls = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.Kind == AssetKind.Url, ct)
                        .ConfigureAwait(false);

                    var subdomainsDiscovered = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.Kind == AssetKind.Subdomain, ct)
                        .ConfigureAwait(false);
                    
                    var lastAssetCreatedAt = await db.Assets.AsNoTracking()
                        .OrderByDescending(a => a.DiscoveredAtUtc)
                        .Select(a => (DateTimeOffset?)a.DiscoveredAtUtc)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new {
                        TotalTargets = totalTargets,
                        TotalAssetsConfirmed = totalAssetsConfirmed,
                        TotalUrls = totalUrls,
                        SubdomainsDiscovered = subdomainsDiscovered,
                        LastAssetCreatedAt = lastAssetCreatedAt
                    });
                })
            .WithName("OpsOverview");

        return app;
    }
}
