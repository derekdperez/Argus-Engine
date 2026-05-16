using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.Discovery.Api.Endpoints;

public static class TechnologyIdentificationEndpoints
{
    public static IEndpointRouteBuilder MapTechnologyIdentificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/technology-identification/targets", QueryTargetsAsync)
            .WithName("ListTechnologyIdentificationTargets");

        app.MapGet("/api/technology-identification/subdomains", QuerySubdomainsAsync)
            .WithName("ListTechnologyIdentificationSubdomains");

        app.MapGet("/api/technology-identification/technologies", QueryTechnologiesAsync)
            .WithName("ListTechnologyIdentificationRows");

        app.MapGet("/api/technology-identification/usage", QueryTechnologyUsageAsync)
            .WithName("ListTechnologyUsage");

        return app;
    }

    private static async Task<IResult> QueryTargetsAsync(ArgusDbContext db, CancellationToken ct)
    {
        var targets = await db.Targets.AsNoTracking()
            .OrderBy(t => t.RootDomain)
            .Select(t => new { t.Id, t.RootDomain, t.GlobalMaxDepth })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targetIds = targets.Select(t => t.Id).ToArray();
        var subdomainCounts = await db.Assets.AsNoTracking()
            .Where(a => targetIds.Contains(a.TargetId)
                && a.Kind == AssetKind.Subdomain
                && a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
            .GroupBy(a => a.TargetId)
            .Select(g => new { TargetId = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.TargetId, x => x.Count, ct)
            .ConfigureAwait(false);

        var technologyRows = await QueryTechnologyRowsAsync(db, null, null, null, ct).ConfigureAwait(false);
        var technologyRollups = technologyRows
            .GroupBy(x => x.TargetId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    TechnologyCount = g.Select(x => TechnologyIdentityKey(x.TechnologyName, x.Version)).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    ObservationCount = g.LongCount(),
                    LastObservedAtUtc = g.Max(x => (DateTimeOffset?)x.LastSeenUtc),
                });

        var rows = targets
            .Select(t =>
            {
                technologyRollups.TryGetValue(t.Id, out var rollup);
                subdomainCounts.TryGetValue(t.Id, out var subdomains);
                return new TechnologyIdentificationTargetDto(
                    t.Id,
                    t.RootDomain,
                    t.GlobalMaxDepth,
                    subdomains,
                    rollup?.TechnologyCount ?? 0,
                    rollup?.ObservationCount ?? 0,
                    rollup?.LastObservedAtUtc);
            })
            .ToArray();

        return Results.Ok(rows);
    }

    private static async Task<IResult> QuerySubdomainsAsync(
        ArgusDbContext db,
        Guid? targetId,
        int? take,
        string? search,
        CancellationToken ct)
    {
        var limit = Math.Clamp(take ?? 250, 1, 500);
        var searchLower = string.IsNullOrWhiteSpace(search) ? null : search.ToLowerInvariant();

        var subdomainQuery = db.Assets.AsNoTracking()
            .Where(a => a.Kind == AssetKind.Subdomain && a.LifecycleStatus == AssetLifecycleStatus.Confirmed);

        if (targetId is { } selectedTargetId)
        {
            subdomainQuery = subdomainQuery.Where(a => a.TargetId == selectedTargetId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            subdomainQuery = subdomainQuery.Where(a => a.CanonicalKey!.ToLowerInvariant().Contains(searchLower!, StringComparison.OrdinalIgnoreCase) || a.RawValue!.ToLowerInvariant().Contains(searchLower!, StringComparison.OrdinalIgnoreCase));
        }

        var subdomainAssets = await subdomainQuery
            .Select(a => new { a.TargetId, a.Id, a.CanonicalKey, a.RawValue })
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var technologyRows = await QueryTechnologyRowsAsync(db, targetId, null, null, ct).ConfigureAwait(false);
        var technologyRollups = technologyRows
            .GroupBy(x => new { x.TargetId, Subdomain = NormalizeHostLike(x.Subdomain) })
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    TechnologyCount = g.Select(x => TechnologyIdentityKey(x.TechnologyName, x.Version)).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                    ObservationCount = g.LongCount(),
                    LastObservedAtUtc = g.Max(x => (DateTimeOffset?)x.LastSeenUtc),
                });

        var rows = subdomainAssets
            .Select(a =>
            {
                var subdomain = NormalizeHostLike(!string.IsNullOrWhiteSpace(a.CanonicalKey) ? a.CanonicalKey : a.RawValue);
                technologyRollups.TryGetValue(new { a.TargetId, Subdomain = subdomain }, out var rollup);
                return new TechnologyIdentificationSubdomainDto(
                    a.TargetId,
                    a.Id,
                    subdomain,
                    rollup?.TechnologyCount ?? 0,
                    rollup?.ObservationCount ?? 0,
                    rollup?.LastObservedAtUtc);
            })
            .Concat(
                technologyRollups
                    .Where(r => subdomainAssets.All(a => a.TargetId != r.Key.TargetId || !string.Equals(NormalizeHostLike(a.CanonicalKey), r.Key.Subdomain, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => new TechnologyIdentificationSubdomainDto(
                        r.Key.TargetId,
                        Guid.Empty,
                        r.Key.Subdomain,
                        r.Value.TechnologyCount,
                        r.Value.ObservationCount,
                        r.Value.LastObservedAtUtc)))
            .OrderBy(x => x.Subdomain, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Results.Ok(rows);
    }

    private static async Task<IResult> QueryTechnologiesAsync(
        ArgusDbContext db,
        Guid? targetId,
        string? subdomain,
        string? search,
        int? take,
        CancellationToken ct)
    {
        var rows = await QueryTechnologyRowsAsync(db, targetId, subdomain, take, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            rows = rows
                .Where(r =>
                    Contains(r.TargetRootDomain, needle)
                    || Contains(r.Subdomain, needle)
                    || Contains(r.AssetCanonicalKey, needle)
                    || Contains(r.TechnologyName, needle)
                    || Contains(r.Vendor, needle)
                    || Contains(r.Product, needle)
                    || Contains(r.Version, needle)
                    || Contains(r.SourceType, needle)
                    || Contains(r.DetectionMode, needle)
                    || Contains(r.FingerprintId, needle)
                    || Contains(r.EvidenceSummary, needle))
                .ToArray();
        }

        return Results.Ok(rows.OrderByDescending(x => x.LastSeenUtc).ThenBy(x => x.TechnologyName).ToArray());
    }

    private static async Task<IResult> QueryTechnologyUsageAsync(
        ArgusDbContext db,
        Guid? targetId,
        int? take,
        CancellationToken ct)
    {
        var rows = await QueryTechnologyRowsAsync(db, targetId, null, take, ct).ConfigureAwait(false);
        var usage = rows
            .GroupBy(r => new
            {
                Name = NormalizeTechnologyName(r.TechnologyName),
                Version = NormalizeVersion(r.Version)
            })
            .Select(g =>
            {
                var locations = g
                    .Select(r => new TechnologyUsageLocationDto(r.TargetId, r.TargetRootDomain, NormalizeHostLike(r.Subdomain)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Subdomain))
                    .DistinctBy(x => $"{x.TargetId:N}|{x.Subdomain}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.TargetRootDomain, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Subdomain, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new TechnologyUsageDto(
                    g.Key.Name,
                    string.IsNullOrWhiteSpace(g.Key.Version) ? null : g.Key.Version,
                    locations.Select(x => x.TargetId).Distinct().LongCount(),
                    locations.LongLength,
                    locations);
            })
            .OrderByDescending(x => x.SubdomainCount)
            .ThenBy(x => x.TechnologyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Results.Ok(usage);
    }

    private static async Task<IReadOnlyList<TechnologyIdentificationRowDto>> QueryTechnologyRowsAsync(
        ArgusDbContext db,
        Guid? targetId,
        string? subdomain,
        int? take,
        CancellationToken ct)
    {
        var observationQuery =
            from o in db.TechnologyObservations.AsNoTracking()
            join t in db.Targets.AsNoTracking() on o.TargetId equals t.Id
            join a in db.Assets.AsNoTracking() on o.AssetId equals a.Id
            select new TechnologyRowProjection(
                o.Id,
                o.TargetId,
                t.RootDomain,
                o.AssetId,
                a.Kind,
                a.CanonicalKey,
                a.RawValue,
                a.FinalUrl,
                o.TechnologyName,
                o.Vendor,
                o.Product,
                o.Version,
                o.ConfidenceScore,
                o.SourceType,
                o.DetectionMode,
                o.FingerprintId,
                o.CatalogHash,
                o.FirstSeenUtc,
                o.LastSeenUtc,
                db.TechnologyObservationEvidence
                    .Where(e => e.ObservationId == o.Id)
                    .OrderByDescending(e => e.CreatedAtUtc)
                    .Select(e => (e.EvidenceKey ?? e.EvidenceType) + ": " + (e.MatchedValueRedacted ?? "matched"))
                    .FirstOrDefault(),
                "observation");

        if (targetId is { } selectedTargetId)
        {
            observationQuery = observationQuery.Where(x => x.TargetId == selectedTargetId);
        }

        List<TechnologyRowProjection> projections;
        try
        {
            projections = await observationQuery
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch
        {
            projections = [];
        }

        var legacyQuery =
            from d in db.TechnologyDetections.AsNoTracking()
            join t in db.Targets.AsNoTracking() on d.TargetId equals t.Id
            join a in db.Assets.AsNoTracking() on d.AssetId equals a.Id
            select new TechnologyRowProjection(
                d.Id,
                d.TargetId,
                t.RootDomain,
                d.AssetId,
                a.Kind,
                a.CanonicalKey,
                a.RawValue,
                a.FinalUrl,
                d.TechnologyName,
                null,
                d.TechnologyName,
                d.Version,
                d.Confidence,
                d.EvidenceSource,
                "legacy-detection",
                d.Pattern ?? d.EvidenceHash,
                "",
                d.DetectedAtUtc,
                d.DetectedAtUtc,
                d.MatchedText ?? d.EvidenceKey,
                "legacy-detection");

        if (targetId is { } legacyTargetId)
        {
            legacyQuery = legacyQuery.Where(x => x.TargetId == legacyTargetId);
        }

        try
        {
            projections.AddRange(await legacyQuery.ToListAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            // A partially migrated deployment should still render the page with empty data.
        }

        var rows = projections
            .Select(x =>
            {
                var derivedSubdomain = DeriveSubdomain(x.AssetKind, x.AssetCanonicalKey, x.AssetRawValue, x.AssetFinalUrl);
                return new TechnologyIdentificationRowDto(
                    x.Id,
                    x.TargetId,
                    x.TargetRootDomain,
                    x.AssetId,
                    x.AssetCanonicalKey,
                    derivedSubdomain,
                    x.TechnologyName,
                    x.Vendor,
                    x.Product,
                    x.Version,
                    x.Confidence,
                    x.SourceType,
                    x.DetectionMode,
                    x.FingerprintId,
                    x.CatalogHash,
                    x.FirstSeenUtc,
                    x.LastSeenUtc,
                    x.EvidenceSummary,
                    x.DataSource);
            })
            .ToArray();

        IReadOnlyList<TechnologyIdentificationRowDto> filteredRows = rows;
        if (!string.IsNullOrWhiteSpace(subdomain))
        {
            var normalizedSubdomain = NormalizeHostLike(subdomain);
            filteredRows = rows
                .Where(x => string.Equals(NormalizeHostLike(x.Subdomain), normalizedSubdomain, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (take is > 0)
        {
            var bounded = Math.Clamp(take.Value, 1, 100_000);
            filteredRows = filteredRows
                .OrderByDescending(x => x.LastSeenUtc)
                .ThenBy(x => x.TechnologyName, StringComparer.OrdinalIgnoreCase)
                .Take(bounded)
                .ToArray();
        }

        return filteredRows;
    }

    private static string DeriveSubdomain(AssetKind kind, string canonicalKey, string rawValue, string? finalUrl)
    {
        if (kind is AssetKind.Domain or AssetKind.Subdomain)
        {
            return NormalizeHostLike(!string.IsNullOrWhiteSpace(canonicalKey) ? canonicalKey : rawValue);
        }

        var host = HostFromUri(finalUrl) ?? HostFromUri(canonicalKey) ?? HostFromUri(rawValue);
        if (!string.IsNullOrWhiteSpace(host))
        {
            return NormalizeHostLike(host);
        }

        var candidate = !string.IsNullOrWhiteSpace(canonicalKey) ? canonicalKey : rawValue;
        var slashIndex = candidate.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            candidate = candidate[..slashIndex];
        }

        var colonIndex = candidate.LastIndexOf(':');
        if (colonIndex > 0)
        {
            candidate = candidate[..colonIndex];
        }

        return NormalizeHostLike(candidate);
    }

    private static string? HostFromUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string NormalizeHostLike(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().TrimEnd('.').ToLowerInvariant();

    private static string TechnologyIdentityKey(string technologyName, string? version) =>
        string.IsNullOrWhiteSpace(version) ? NormalizeTechnologyName(technologyName) : $"{NormalizeTechnologyName(technologyName)}|{NormalizeVersion(version)}";

    private static string NormalizeTechnologyName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    private static string NormalizeVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private sealed record TechnologyRowProjection(
        Guid Id,
        Guid TargetId,
        string TargetRootDomain,
        Guid AssetId,
        AssetKind AssetKind,
        string AssetCanonicalKey,
        string AssetRawValue,
        string? AssetFinalUrl,
        string TechnologyName,
        string? Vendor,
        string? Product,
        string? Version,
        decimal Confidence,
        string SourceType,
        string DetectionMode,
        string FingerprintId,
        string CatalogHash,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        string? EvidenceSummary,
        string DataSource);
}



