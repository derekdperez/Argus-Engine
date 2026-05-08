using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class TagEndpoints
{
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/tags",
                async (ArgusDbContext db, string? type, CancellationToken ct) =>
                {
                    var q = db.Tags.AsNoTracking().Where(t => t.IsActive);
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        var tagType = type.Trim();
                        q = q.Where(t => t.TagType == tagType);
                    }

                    var rows = await q.OrderBy(t => t.Name)
                        .Select(t => new
                        {
                            t.Id,
                            t.Slug,
                            t.Name,
                            t.TagType,
                            t.Source,
                            t.Description,
                            t.Website,
                        })
                        .Take(5000)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListTags");

        app.MapGet(
                "/api/assets/{assetId:guid}/tags",
                async (Guid assetId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var rows = await db.AssetTags.AsNoTracking()
                        .Where(at => at.AssetId == assetId)
                        .Join(
                            db.Tags.AsNoTracking(),
                            at => at.TagId,
                            tag => tag.Id,
                            (at, tag) => new AssetTagDto(
                                tag.Id,
                                tag.Slug,
                                tag.Name,
                                tag.TagType,
                                at.Confidence,
                                at.EvidenceJson,
                                at.FirstSeenAtUtc,
                                at.LastSeenAtUtc))
                        .OrderByDescending(t => t.Confidence)
                        .ThenBy(t => t.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListAssetTags");

        app.MapGet(
                "/api/targets/{targetId:guid}/technologies",
                async (Guid targetId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var rows = await db.TechnologyObservations.AsNoTracking()
                        .Where(o => o.TargetId == targetId)
                        .GroupBy(o => new { o.TechnologyName, o.Vendor, o.Product })
                        .Select(g => new TargetTechnologyDto(
                            Guid.Empty,
                            "",
                            g.Key.TechnologyName,
                            TechnologyConstants.TagType,
                            g.Select(x => x.AssetId).Distinct().LongCount(),
                            g.Max(x => x.ConfidenceScore),
                            g.Max(x => x.LastSeenUtc)))
                        .OrderByDescending(x => x.AssetCount)
                        .ThenBy(x => x.Name)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListTargetTechnologies");

        app.MapGet(
                "/api/technologies",
                async (ArgusDbContext db, int? take, CancellationToken ct) =>
                {
                    var maxRows = take is > 0 ? take.Value : int.MaxValue;
                    var q = from o in db.TechnologyObservations.AsNoTracking()
                            join t in db.Targets.AsNoTracking() on o.TargetId equals t.Id
                            join a in db.Assets.AsNoTracking() on o.AssetId equals a.Id
                            select new TechnologyDetectionRowDto(
                                o.Id,
                                t.Id,
                                t.RootDomain,
                                a.Id,
                                a.CanonicalKey,
                                o.TechnologyName,
                                o.SourceType,
                                o.Version,
                                o.DetectionMode,
                                o.FingerprintId,
                                null,
                                db.TechnologyObservationEvidence
                                    .Where(e => e.ObservationId == o.Id)
                                    .OrderByDescending(e => e.CreatedAtUtc)
                                    .Select(e => e.MatchedValueRedacted)
                                    .FirstOrDefault(),
                                o.ConfidenceScore,
                                o.LastSeenUtc);

                    IQueryable<TechnologyDetectionRowDto> ordered = q.OrderByDescending(x => x.DetectedAtUtc);
                    if (take is > 0)
                    {
                        ordered = ordered.Take(Math.Clamp(maxRows, 1, 1_000_000));
                    }

                    var rows = await ordered
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListTechnologies");
        return app;
    }
}




