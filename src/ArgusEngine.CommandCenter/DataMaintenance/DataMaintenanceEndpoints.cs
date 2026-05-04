using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Application.Events;
using ArgusEngine.CommandCenter;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.DataMaintenance;

public static class DataMaintenanceEndpoints
{
    private const string ConfigEnabled = "Nightmare:DataMaintenance:Enabled";
    private const string ConfigApiKey = "Nightmare:DataMaintenance:ApiKey";

    public const string PhraseClearAllTargets = "DELETE ALL TARGETS";
    public const string PhraseClearAllAssets = "DELETE ALL ASSETS";
    public const string PhraseClearAssetsForDomain = "DELETE ASSETS FOR DOMAIN";
    public const string PhraseClearAssetsFiltered = "DELETE FILTERED ASSETS";

    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/api/maintenance/status",
                (IConfiguration config) =>
                {
                    var enabled = config.GetValue(ConfigEnabled, false);
                    var key = config[ConfigApiKey]?.Trim();
                    return Results.Ok(new MaintenanceStatusDto(enabled, !string.IsNullOrEmpty(key)));
                })
            .WithName("MaintenanceStatus")
            .AllowAnonymous();

        app.MapPost(
                "/api/maintenance/clear-all-targets",
                async (
                    MaintenancePhraseBody body,
                    HttpRequest http,
                    IConfiguration config,
                    ArgusDbContext db,
                    IAssetDeduplicator dedup,
                    CancellationToken ct) =>
                {
                    if (!IsAllowed(config, http))
                        return MaintenanceDenied(config);

                    if (body is null || !string.Equals(body.ConfirmationPhrase?.Trim(), PhraseClearAllTargets, StringComparison.Ordinal))
                        return Results.BadRequest($"confirmationPhrase must be exactly: {PhraseClearAllTargets}");

                    await dedup.ClearAllAsync(ct).ConfigureAwait(false);
                    var n = await db.Targets.ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new MaintenanceDeleteResult("clear-all-targets", n, "Cascaded assets and high-value findings per FK."));
                })
            .WithName("MaintenanceClearAllTargets")
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost(
                "/api/maintenance/clear-all-assets",
                async (
                    MaintenancePhraseBody body,
                    HttpRequest http,
                    IConfiguration config,
                    ArgusDbContext db,
                    IAssetDeduplicator dedup,
                    CancellationToken ct) =>
                {
                    if (!IsAllowed(config, http))
                        return MaintenanceDenied(config);

                    if (body is null || !string.Equals(body.ConfirmationPhrase?.Trim(), PhraseClearAllAssets, StringComparison.Ordinal))
                        return Results.BadRequest($"confirmationPhrase must be exactly: {PhraseClearAllAssets}");

                    await dedup.ClearAllAsync(ct).ConfigureAwait(false);
                    var findings = await db.HighValueFindings.ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    var n = await db.Assets.ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new MaintenanceDeleteResult("clear-all-assets", n, $"Targets and worker switches unchanged. Deleted high-value findings: {findings}."));
                })
            .WithName("MaintenanceClearAllAssets")
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost(
                "/api/maintenance/clear-assets-for-domain",
                async (
                    MaintenanceClearDomainBody body,
                    HttpRequest http,
                    IConfiguration config,
                    ArgusDbContext db,
                    IAssetDeduplicator dedup,
                    CancellationToken ct) =>
                {
                    if (!IsAllowed(config, http))
                        return MaintenanceDenied(config);

                    if (body is null || !string.Equals(body.ConfirmationPhrase?.Trim(), PhraseClearAssetsForDomain, StringComparison.Ordinal))
                        return Results.BadRequest($"confirmationPhrase must be exactly: {PhraseClearAssetsForDomain}");

                    if (!TargetRootNormalization.TryNormalize(body.RootDomain ?? "", out var root))
                        return Results.BadRequest("rootDomain required");

                    var targetIds = await db.Targets.AsNoTracking()
                        .Where(t => t.RootDomain == root)
                        .Select(t => t.Id)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    if (targetIds.Count == 0)
                        return Results.Ok(new MaintenanceDeleteResult("clear-assets-for-domain", 0, $"No target with root domain {root}."));

                    foreach (var tid in targetIds)
                        await dedup.ClearForTargetAsync(tid, ct).ConfigureAwait(false);

                    var findings = await db.HighValueFindings
                        .Where(f => targetIds.Contains(f.TargetId))
                        .ExecuteDeleteAsync(ct)
                        .ConfigureAwait(false);
                    var n = await db.Assets.Where(a => targetIds.Contains(a.TargetId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    return Results.Ok(new MaintenanceDeleteResult("clear-assets-for-domain", n, $"Root domain {root}. Deleted high-value findings: {findings}."));
                })
            .WithName("MaintenanceClearAssetsForDomain")
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost(
                "/api/maintenance/clear-assets-filtered",
                async (
                    MaintenanceClearAssetsFilteredBody body,
                    HttpRequest http,
                    IConfiguration config,
                    ArgusDbContext db,
                    IAssetDeduplicator dedup,
                    CancellationToken ct) =>
                {
                    if (!IsAllowed(config, http))
                        return MaintenanceDenied(config);

                    if (body is null || !string.Equals(body.ConfirmationPhrase?.Trim(), PhraseClearAssetsFiltered, StringComparison.Ordinal))
                        return Results.BadRequest($"confirmationPhrase must be exactly: {PhraseClearAssetsFiltered}");

                    var q = db.Assets.AsQueryable();
                    var anyFilter = false;

                    if (!string.IsNullOrWhiteSpace(body.Kind))
                    {
                        if (!TryParseAssetKind(body.Kind, out var kind))
                            return Results.BadRequest("kind must be an AssetKind name (e.g. Url) or numeric value.");
                        q = q.Where(a => a.Kind == kind);
                        anyFilter = true;
                    }

                    if (!string.IsNullOrWhiteSpace(body.LifecycleStatus))
                    {
                        var status = body.LifecycleStatus.Trim();
                        q = q.Where(a => a.LifecycleStatus == status);
                        anyFilter = true;
                    }

                    if (!string.IsNullOrWhiteSpace(body.DiscoveredByContains))
                    {
                        var by = body.DiscoveredByContains.Trim();
                        q = q.Where(a => EF.Functions.ILike(a.DiscoveredBy, $"%{by}%"));
                        anyFilter = true;
                    }

                    if (!anyFilter)
                        return Results.BadRequest("Provide at least one filter: kind, lifecycleStatus, discoveredByContains.");

                    var targetIds = await q.Select(a => a.TargetId).Distinct().ToListAsync(ct).ConfigureAwait(false);
                    var assetIds = await q.Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
                    var findings = assetIds.Count == 0
                        ? 0
                        : await db.HighValueFindings
                            .Where(f => f.SourceAssetId != null && assetIds.Contains(f.SourceAssetId.Value))
                            .ExecuteDeleteAsync(ct)
                            .ConfigureAwait(false);
                    var deleted = await q.ExecuteDeleteAsync(ct).ConfigureAwait(false);

                    foreach (var targetId in targetIds)
                        await dedup.ClearForTargetAsync(targetId, ct).ConfigureAwait(false);

                    return Results.Ok(
                        new MaintenanceDeleteResult(
                            "clear-assets-filtered",
                            deleted,
                            $"Filters: kind={body.Kind ?? "*"}, status={body.LifecycleStatus ?? "*"}, discoveredByContains={body.DiscoveredByContains ?? "*"}. Deleted high-value findings: {findings}"));
                })
            .WithName("MaintenanceClearAssetsFiltered")
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost(
                "/api/maintenance/queue-enumeration-job",
                async (
                    MaintenanceQueueEnumerationJobBody body,
                    HttpRequest http,
                    IConfiguration config,
                    ArgusDbContext db,
                    IEventOutbox outbox,
                    CancellationToken ct) =>
                {
                    if (!IsAllowed(config, http))
                        return MaintenanceDenied(config);

                    var provider = body.Provider?.Trim().ToLowerInvariant();
                    if (provider is not ("subfinder" or "amass"))
                        return Results.BadRequest("provider must be subfinder or amass");

                    var target = await db.Targets.AsNoTracking()
                        .Where(t => t.Id == body.TargetId)
                        .Select(t => new { t.Id, t.RootDomain })
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    if (target is null)
                        return Results.NotFound($"Target {body.TargetId} was not found.");

                    var correlation = Guid.NewGuid();
                    await outbox.EnqueueAsync(
                            new SubdomainEnumerationRequested(
                                target.Id,
                                target.RootDomain,
                                provider,
                                RequestedBy: string.IsNullOrWhiteSpace(body.RequestedBy) ? "manual" : body.RequestedBy.Trim(),
                                RequestedAt: DateTimeOffset.UtcNow,
                                CorrelationId: correlation,
                                EventId: Guid.NewGuid(),
                                CausationId: correlation,
                                Producer: "command-center"),
                            ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new
                    {
                        queued = true,
                        provider,
                        targetId = target.Id,
                        rootDomain = target.RootDomain,
                    });
                })
            .WithName("MaintenanceQueueEnumerationJob")
            .DisableAntiforgery()
            .AllowAnonymous();
    }

    private static bool TryParseAssetKind(string raw, out AssetKind kind)
    {
        var trimmed = raw.Trim();
        if (Enum.TryParse<AssetKind>(trimmed, ignoreCase: true, out kind))
            return true;
        if (int.TryParse(trimmed, out var n) && Enum.IsDefined(typeof(AssetKind), n))
        {
            kind = (AssetKind)n;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool IsAllowed(IConfiguration config, HttpRequest http)
    {
        if (!config.GetValue(ConfigEnabled, false))
            return false;
        var required = config[ConfigApiKey]?.Trim();
        if (string.IsNullOrWhiteSpace(required))
            return false;
        return string.Equals(http.Headers["X-Nightmare-Maintenance-Key"].ToString(), required, StringComparison.Ordinal);
    }

    private static IResult MaintenanceDenied(IConfiguration config)
    {
        if (!config.GetValue(ConfigEnabled, false))
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(config[ConfigApiKey]))
        {
            return Results.Problem(
                title: "Maintenance endpoints misconfigured",
                detail: "Nightmare:DataMaintenance:Enabled=true requires Nightmare:DataMaintenance:ApiKey to be configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Unauthorized();
    }
}
