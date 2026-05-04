using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class TagEndpoints
{
    public static void Map(WebApplication app)
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
                    var rows = await db.AssetTags.AsNoTracking()
                        .Where(at => at.TargetId == targetId)
                        .Join(
                            db.Tags.AsNoTracking().Where(t => t.TagType == TechnologyConstants.TagType),
                            at => at.TagId,
                            tag => tag.Id,
                            (at, tag) => new { AssetTag = at, Tag = tag })
                        .GroupBy(x => new { x.Tag.Id, x.Tag.Slug, x.Tag.Name, x.Tag.TagType })
                        .Select(g => new TargetTechnologyDto(
                            g.Key.Id,
                            g.Key.Slug,
                            g.Key.Name,
                            g.Key.TagType,
                            g.LongCount(),
                            g.Max(x => x.AssetTag.Confidence),
                            g.Max(x => x.AssetTag.LastSeenAtUtc)))
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
                    var maxRows = take ?? 10000;
                    var q = from d in db.TechnologyDetections.AsNoTracking()
                            join t in db.Targets.AsNoTracking() on d.TargetId equals t.Id
                            join a in db.Assets.AsNoTracking() on d.AssetId equals a.Id
                            join tag in db.Tags.AsNoTracking() on d.TagId equals tag.Id
                            select new ArgusEngine.CommandCenter.Models.TechnologyDetectionRowDto(
                                d.Id,
                                t.Id,
                                t.RootDomain,
                                a.Id,
                                a.CanonicalKey,
                                d.TechnologyName,
                                tag.Name, // Using tag.Name as Category name placeholder or just Tag Name if categories are embedded in metadata
                                d.Version,
                                d.EvidenceSource,
                                d.EvidenceKey,
                                d.Pattern,
                                d.MatchedText,
                                d.Confidence,
                                d.DetectedAtUtc);

                    var rows = await q.OrderByDescending(x => x.DetectedAtUtc)
                        .Take(maxRows)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListTechnologies");
    }
}
