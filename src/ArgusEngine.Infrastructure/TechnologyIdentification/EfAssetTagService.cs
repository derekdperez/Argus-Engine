using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.TechnologyIdentification;

public sealed class EfAssetTagService(ArgusDbContext db) : IAssetTagService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task SeedTechnologyTagsAsync(
        IReadOnlyCollection<TechnologyDefinition> technologies,
        CancellationToken cancellationToken = default)
    {
        if (technologies.Count == 0)
            return;

        var slugs = technologies
            .Select(t => TechnologyTagSlug.FromName(t.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var existingRows = await db.Tags.AsNoTracking()
            .Where(t => slugs.Contains(t.Slug))
            .Select(t => t.Slug)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = existingRows.ToHashSet(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        foreach (var technology in technologies)
        {
            var slug = TechnologyTagSlug.FromName(technology.Name);
            if (existing.Contains(slug))
                continue;

            db.Tags.Add(
                new Tag
                {
                    Id = Guid.NewGuid(),
                    Slug = slug,
                    Name = technology.Name,
                    TagType = TechnologyConstants.TagType,
                    Source = TechnologyConstants.CatalogSource,
                    SourceKey = technology.Name,
                    Description = technology.Description,
                    Website = technology.Website,
                    MetadataJson = technology.MetadataJson,
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            existing.Add(slug);
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetTagPersistenceResult> UpsertTechnologyDetectionsAsync(
        Guid targetId,
        Guid assetId,
        IReadOnlyCollection<TechnologyScanResult> results,
        IReadOnlyDictionary<string, TechnologyDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
            return new AssetTagPersistenceResult(0, 0, 0);

        var grouped = results
            .GroupBy(r => r.TechnologyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var attached = 0;
        var evidenceCount = 0;
        foreach (var group in grouped)
        {
            var definition = definitions.TryGetValue(group.Key, out var exact)
                ? exact
                : new TechnologyDefinition(group.Key, null, null, [], [], [], [], [], "{}");

            var tagId = await UpsertTechnologyTagAsync(definition, cancellationToken).ConfigureAwait(false);
            var groupResults = group.ToArray();
            var confidence = groupResults.Max(x => x.Confidence) / 100.0m;
            var evidenceJson = JsonSerializer.Serialize(
                groupResults.Select(x => new
                {
                    x.EvidenceSource,
                    x.EvidenceKey,
                    x.Pattern,
                    x.MatchedText,
                    x.Version,
                    x.Confidence,
                    x.IsImplied,
                }),
                JsonOptions);

            await UpsertAssetTagAsync(
                    targetId,
                    assetId,
                    tagId,
                    confidence,
                    evidenceJson,
                    cancellationToken)
                .ConfigureAwait(false);
            attached++;

            foreach (var result in groupResults)
            {
                await UpsertDetectionAsync(
                        targetId,
                        assetId,
                        tagId,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
                evidenceCount++;
            }
        }

        return new AssetTagPersistenceResult(grouped.Length, evidenceCount, attached);
    }

    private async Task<Guid> UpsertTechnologyTagAsync(TechnologyDefinition technology, CancellationToken ct)
    {
        var slug = TechnologyTagSlug.FromName(technology.Name);
        var now = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tags (
                    id,
                    slug,
                    name,
                    tag_type,
                    source,
                    source_key,
                    description,
                    website,
                    metadata_json,
                    is_active,
                    created_at_utc,
                    updated_at_utc)
                VALUES (
                    {Guid.NewGuid()},
                    {slug},
                    {technology.Name},
                    {TechnologyConstants.TagType},
                    {TechnologyConstants.CatalogSource},
                    {technology.Name},
                    {technology.Description},
                    {technology.Website},
                    {technology.MetadataJson}::jsonb,
                    true,
                    {now},
                    {now})
                ON CONFLICT (slug) DO UPDATE SET
                    name = EXCLUDED.name,
                    tag_type = EXCLUDED.tag_type,
                    source = EXCLUDED.source,
                    source_key = EXCLUDED.source_key,
                    description = COALESCE(EXCLUDED.description, tags.description),
                    website = COALESCE(EXCLUDED.website, tags.website),
                    metadata_json = COALESCE(EXCLUDED.metadata_json, tags.metadata_json),
                    is_active = true,
                    updated_at_utc = EXCLUDED.updated_at_utc;
                """,
                ct)
            .ConfigureAwait(false);

        return await db.Tags.AsNoTracking()
            .Where(t => t.Slug == slug)
            .Select(t => t.Id)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task UpsertAssetTagAsync(
        Guid targetId,
        Guid assetId,
        Guid tagId,
        decimal confidence,
        string evidenceJson,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO asset_tags (
                    id,
                    target_id,
                    asset_id,
                    tag_id,
                    confidence,
                    source,
                    evidence_json,
                    first_seen_at_utc,
                    last_seen_at_utc)
                VALUES (
                    {Guid.NewGuid()},
                    {targetId},
                    {assetId},
                    {tagId},
                    {confidence},
                    {TechnologyConstants.DetectionSource},
                    {evidenceJson}::jsonb,
                    {now},
                    {now})
                ON CONFLICT (asset_id, tag_id) DO UPDATE SET
                    confidence = GREATEST(asset_tags.confidence, EXCLUDED.confidence),
                    source = EXCLUDED.source,
                    evidence_json = EXCLUDED.evidence_json,
                    last_seen_at_utc = EXCLUDED.last_seen_at_utc;
                """,
                ct)
            .ConfigureAwait(false);
    }

    private async Task UpsertDetectionAsync(
        Guid targetId,
        Guid assetId,
        Guid tagId,
        TechnologyScanResult result,
        CancellationToken ct)
    {
        var evidenceHash = HashEvidence(assetId, result);
        await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO technology_detections (
                    id,
                    target_id,
                    asset_id,
                    tag_id,
                    technology_name,
                    evidence_source,
                    evidence_key,
                    pattern,
                    matched_text,
                    version,
                    confidence,
                    evidence_hash,
                    detected_at_utc)
                VALUES (
                    {Guid.NewGuid()},
                    {targetId},
                    {assetId},
                    {tagId},
                    {result.TechnologyName},
                    {result.EvidenceSource},
                    {result.EvidenceKey},
                    {Cap(result.Pattern, 2048)},
                    {Cap(result.MatchedText, 512)},
                    {Cap(result.Version, 128)},
                    {result.Confidence / 100.0m},
                    {evidenceHash},
                    {DateTimeOffset.UtcNow})
                ON CONFLICT (asset_id, tag_id, evidence_hash) DO UPDATE SET
                    detected_at_utc = EXCLUDED.detected_at_utc;
                """,
                ct)
            .ConfigureAwait(false);
    }

    private static string HashEvidence(Guid assetId, TechnologyScanResult result)
    {
        var text = string.Join(
            "|",
            assetId.ToString("D"),
            result.TechnologyName,
            result.EvidenceSource,
            result.EvidenceKey ?? "",
            result.Pattern ?? "",
            result.MatchedText ?? "",
            result.Version ?? "");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string? Cap(string? value, int maxLength)
    {
        if (value is null)
            return null;

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
