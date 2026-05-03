using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class HighValueFindingEndpoints
{
    public static IEndpointRouteBuilder MapHighValueFindingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/high-value-findings",
                async (ArgusDbContext db, bool? criticalOnly, int? take, CancellationToken ct) =>
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
                            SourceUrl = a != null ? a.CanonicalKey : null
                        };

                    if (criticalOnly == true)
                        q = q.Where(x => x.f.Severity == "Critical");

                    var list = await q
                        .OrderByDescending(x => x.f.DiscoveredAtUtc)
                        .Take(take ?? 100)
                        .Select(x => new HighValueFindingRowDto(
                            x.f.Id,
                            x.f.TargetId,
                            x.f.SourceAssetId,
                            x.f.FindingType,
                            x.f.Severity,
                            x.f.PatternName,
                            x.f.Category,
                            x.f.MatchedText,
                            x.SourceUrl ?? "(unknown)",
                            null, null, 0, null,
                            x.f.WorkerName,
                            x.f.ImportanceScore,
                            x.f.DiscoveredAtUtc,
                            x.RootDomain))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(list);
                })
            .WithName("GetHighValueFindings");

        return app;
    }
}
