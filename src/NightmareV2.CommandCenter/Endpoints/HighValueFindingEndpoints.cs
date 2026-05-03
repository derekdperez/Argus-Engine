using Microsoft.EntityFrameworkCore;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class HighValueFindingEndpoints
{
    public static IEndpointRouteBuilder MapHighValueFindingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/high-value-findings",
                async (NightmareDbContext db, bool? criticalOnly, int? take, CancellationToken ct) =>
                {
                    var q =
                        from f in db.HighValueFindings.AsNoTracking()
                        join t in db.Targets.AsNoTracking() on f.TargetId equals t.Id
                        join a in db.Assets.AsNoTracking() on f.SourceAssetId equals (Guid?)a.Id into assetJoin
                        from a in assetJoin.DefaultIfEmpty()
                        select new
                        {
                            f,
                            t.RootDomain,
                            AssetLifecycleStatus = a == null ? null : a.LifecycleStatus,
                            TypeDetailsJson = a == null ? null : a.TypeDetailsJson,
                            AssetRawValue = a == null ? null : a.RawValue,
                            AssetFinalUrl = a == null ? null : a.FinalUrl,
                            AssetRedirectCount = a == null ? 0 : a.RedirectCount,
                            AssetRedirectChainJson = a == null ? null : a.RedirectChainJson,
                        };

                    if (criticalOnly == true)
                        q = q.Where(x => x.f.Severity == "Critical");

                    var ordered = q.OrderByDescending(x => x.f.DiscoveredAtUtc);
                    var rowQuery = take is > 0
                        ? ordered.Take(Math.Clamp(take.Value, 1, 1_000_000))
                        : ordered;

                    var rows = await rowQuery
                        .Select(x => new HighValueFindingRowDto(
                            x.f.Id,
                            x.f.TargetId,
                            x.f.SourceAssetId,
                            x.f.FindingType,
                            x.f.Severity,
                            x.f.PatternName,
                            x.f.Category ?? "",
                            x.f.MatchedText ?? "",
                            string.IsNullOrWhiteSpace(x.AssetFinalUrl) ? x.f.SourceUrl : x.AssetFinalUrl,
                            x.AssetRawValue,
                            x.AssetFinalUrl,
                            x.AssetRedirectCount,
                            x.AssetRedirectChainJson,
                            x.f.WorkerName,
                            x.f.ImportanceScore,
                            x.f.DiscoveredAtUtc,
                            x.RootDomain))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListHighValueFindings");

        return app;
    }

    public static void Map(WebApplication app) => app.MapHighValueFindingEndpoints();
}
