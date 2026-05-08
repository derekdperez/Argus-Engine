using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class HighValueFindingEndpoints
{
    public static IEndpointRouteBuilder MapHighValueFindingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/high-value-findings",
                async (ArgusDbContext db, bool? criticalOnly, bool? includeResolved, int? take, CancellationToken ct) =>
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

                    if (includeResolved != true)
                    {
                        q = q.Where(x =>
                            x.f.IsHighValue
                            && x.AssetLifecycleStatus == AssetLifecycleStatus.Confirmed
                            && x.f.InvestigationStatus != "False Positive"
                            && x.f.InvestigationStatus != "Valid Finding");
                    }

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
                            x.RootDomain,
                            string.IsNullOrWhiteSpace(x.f.InvestigationStatus) ? "Pending" : x.f.InvestigationStatus,
                            x.f.IsHighValue))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListHighValueFindings");

        app.MapGet(
                "/api/high-value-assets",
                async (ArgusDbContext db, bool? includeResolved, int? take, CancellationToken ct) =>
                {
                    var q =
                        from f in db.HighValueFindings.AsNoTracking()
                        join t in db.Targets.AsNoTracking() on f.TargetId equals t.Id
                        join a in db.Assets.AsNoTracking() on f.SourceAssetId equals (Guid?)a.Id
                        where a.LifecycleStatus == AssetLifecycleStatus.Confirmed
                        select new { f, t.RootDomain, a };

                    if (includeResolved != true)
                    {
                        q = q.Where(x =>
                            x.f.IsHighValue
                            && x.f.InvestigationStatus != "False Positive"
                            && x.f.InvestigationStatus != "Valid Finding");
                    }

                    var rawRows = await q
                        .OrderByDescending(x => x.f.DiscoveredAtUtc)
                        .Take(Math.Clamp(take ?? 1_000_000, 1, 1_000_000))
                        .Select(x => new HighValueAssetRowDto(
                            x.a.Id,
                            x.f.Id,
                            x.a.TargetId,
                            x.a.Kind.ToString(),
                            x.a.Category.ToString(),
                            x.a.CanonicalKey,
                            x.a.RawValue,
                            x.a.DisplayName,
                            x.a.Depth,
                            x.a.DiscoveredBy,
                            x.a.DiscoveryContext,
                            x.a.DiscoveredAtUtc,
                            x.a.LastSeenAtUtc,
                            x.a.Confidence,
                            x.a.LifecycleStatus,
                            x.a.TypeDetailsJson,
                            x.a.FinalUrl,
                            x.a.RedirectCount,
                            x.a.RedirectChainJson,
                            x.RootDomain,
                            x.f.FindingType,
                            x.f.Severity,
                            x.f.PatternName,
                            x.f.Category ?? "",
                            x.f.MatchedText ?? "",
                            x.f.WorkerName,
                            x.f.ImportanceScore,
                            x.f.DiscoveredAtUtc,
                            string.IsNullOrWhiteSpace(x.f.InvestigationStatus) ? "Pending" : x.f.InvestigationStatus,
                            x.f.IsHighValue))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var rows = rawRows
                        .GroupBy(x => x.AssetId)
                        .Select(g => g
                            .OrderByDescending(x => x.InvestigationStatus == "Pending")
                            .ThenByDescending(x => x.FindingDiscoveredAtUtc)
                            .First())
                        .OrderByDescending(x => x.FindingDiscoveredAtUtc)
                        .ToArray();

                    return Results.Ok(rows);
                })
            .WithName("ListHighValueAssets");

        app.MapPatch(
                "/api/high-value-findings/{id:guid}/status",
                async (Guid id, HighValueFindingStatusPatch request, ArgusDbContext db, CancellationToken ct) =>
                {
                    var status = NormalizeInvestigationStatus(request.InvestigationStatus);
                    if (status is null)
                        return Results.BadRequest(new { message = "InvestigationStatus must be Pending, In Process, False Positive, or Valid Finding." });

                    var finding = await db.HighValueFindings.FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
                    if (finding is null)
                        return Results.NotFound();

                    finding.InvestigationStatus = status;
                    finding.InvestigationUpdatedAtUtc = DateTimeOffset.UtcNow;
                    if (status == "False Positive")
                        finding.IsHighValue = false;

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new { finding.Id, finding.InvestigationStatus, finding.IsHighValue });
                })
            .WithName("UpdateHighValueFindingStatus");

        app.MapPost(
                "/api/high-value-findings/{id:guid}/false-positive",
                async (Guid id, ArgusDbContext db, CancellationToken ct) =>
                {
                    var finding = await db.HighValueFindings.FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
                    if (finding is null)
                        return Results.NotFound();

                    finding.InvestigationStatus = "False Positive";
                    finding.IsHighValue = false;
                    finding.InvestigationUpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new { finding.Id, finding.InvestigationStatus, finding.IsHighValue });
                })
            .WithName("MarkHighValueFindingFalsePositive");

        app.MapPost(
                "/api/high-value-assets/{assetId:guid}/false-positive",
                async (Guid assetId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var findings = await db.HighValueFindings
                        .Where(f => f.SourceAssetId == assetId)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    if (findings.Count == 0)
                        return Results.NotFound();

                    var now = DateTimeOffset.UtcNow;
                    foreach (var finding in findings)
                    {
                        finding.InvestigationStatus = "False Positive";
                        finding.IsHighValue = false;
                        finding.InvestigationUpdatedAtUtc = now;
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new { AssetId = assetId, RowsAffected = findings.Count });
                })
            .WithName("MarkHighValueAssetFalsePositive");

        return app;
    }

    private static string? NormalizeInvestigationStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "pending" => "Pending",
            "in process" or "in-process" or "inprocess" => "In Process",
            "false positive" or "false-positive" or "falsepositive" => "False Positive",
            "valid finding" or "valid-finding" or "validfinding" => "Valid Finding",
            _ => null,
        };
    }

    public static void Map(WebApplication app) => app.MapHighValueFindingEndpoints();
}


