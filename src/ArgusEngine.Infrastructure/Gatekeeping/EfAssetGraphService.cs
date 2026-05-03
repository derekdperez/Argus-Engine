using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Npgsql;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class EfAssetGraphService(
    ArgusDbContext db,
    IAssetRelationshipValidator relationshipValidator,
    ILogger<EfAssetGraphService> logger) : IAssetGraphService
{
    private static readonly Action<ILogger, Guid, Exception?> LogSkipAssetGraphTargetMissing =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(1, nameof(LogSkipAssetGraphTargetMissing)),
            "Skip asset graph upsert: target {TargetId} not in recon_targets.");

    public async Task<AssetUpsertResult> UpsertAssetAsync(
        AssetDiscovered message,
        CanonicalAsset canonical,
        CancellationToken cancellationToken = default)
    {
        var targetExists = await db.Targets.AsNoTracking()
            .AnyAsync(t => t.Id == message.TargetId, cancellationToken)
            .ConfigureAwait(false);
        if (!targetExists)
        {
            LogSkipAssetGraphTargetMissing(logger, message.TargetId, null);
            return new AssetUpsertResult(Guid.Empty, Inserted: false, RelationshipInserted: false, RelationshipUpdated: false, "target-missing");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = message.OccurredAt == default ? DateTimeOffset.UtcNow : message.OccurredAt;
        var inserted = false;
        var child = await db.Assets
            .FirstOrDefaultAsync(a => a.TargetId == message.TargetId && a.CanonicalKey == canonical.CanonicalKey, cancellationToken)
            .ConfigureAwait(false);

        if (child is null)
        {
            child = new StoredAsset
            {
                Id = message.AssetId is { } provided && provided != Guid.Empty ? provided : Guid.NewGuid(),
                TargetId = message.TargetId,
                Kind = message.Kind,
                Category = AssetKindClassification.CategoryFor(message.Kind),
                CanonicalKey = canonical.CanonicalKey,
                RawValue = message.RawValue,
                DisplayName = ResolveDisplayName(message.Kind, canonical.NormalizedDisplay, message.RawValue),
                Depth = message.Depth,
                DiscoveredBy = Truncate(message.DiscoveredBy, 128),
                DiscoveryContext = Truncate(message.DiscoveryContext ?? "", 512),
                DiscoveredAtUtc = now,
                LastSeenAtUtc = now,
                Confidence = 1.0m,
                LifecycleStatus = ResolveInitialLifecycleStatus(message),
            };

            db.Assets.Add(child);
            EnqueueHttpRequestIfNeeded(child);
            inserted = true;

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
                && pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                DetachPendingAssetGraph(child);
                child = await db.Assets
                    .FirstOrDefaultAsync(a => a.TargetId == message.TargetId && a.CanonicalKey == canonical.CanonicalKey, cancellationToken)
                    .ConfigureAwait(false);

                if (child is null)
                    throw;

                inserted = false;
                await TouchExistingAssetAsync(child, now, message, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await TouchExistingAssetAsync(child, now, message, cancellationToken).ConfigureAwait(false);
        }

        var relationshipInserted = false;
        var relationshipUpdated = false;

        if (message.ParentAssetId is { } parentId && parentId != Guid.Empty)
        {
            var relResult = await UpsertRelationshipInsideTransactionAsync(
                new AssetRelationshipDiscovered
                {
                    TargetId = message.TargetId,
                    ParentAssetId = parentId,
                    ChildAssetId = child.Id,
                    RelationshipType = message.RelationshipType,
                    IsPrimary = message.IsPrimaryRelationship,
                    Confidence = message.Confidence,
                    DiscoveredBy = message.DiscoveredBy,
                    DiscoveryContext = message.DiscoveryContext,
                    OccurredAtUtc = now,
                }, cancellationToken).ConfigureAwait(false);

            relationshipInserted = relResult.Inserted;
            relationshipUpdated = relResult.Updated;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AssetUpsertResult(child.Id, inserted, relationshipInserted, relationshipUpdated);
    }

    public async Task<AssetRelationshipResult> UpsertRelationshipAsync(
        AssetRelationshipDiscovered message,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await UpsertRelationshipInsideTransactionAsync(message, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<AssetRelationshipResult> UpsertRelationshipInsideTransactionAsync(
        AssetRelationshipDiscovered message,
        CancellationToken cancellationToken)
    {
        if (message.ParentAssetId == message.ChildAssetId)
            return new AssetRelationshipResult(null, Inserted: false, Updated: false, "self-reference");

        var parent = await db.Assets
            .FirstOrDefaultAsync(a => a.Id == message.ParentAssetId, cancellationToken)
            .ConfigureAwait(false);
        if (parent is null)
            return new AssetRelationshipResult(null, Inserted: false, Updated: false, "parent-missing");

        var child = await db.Assets
            .FirstOrDefaultAsync(a => a.Id == message.ChildAssetId, cancellationToken)
            .ConfigureAwait(false);
        if (child is null)
            return new AssetRelationshipResult(null, Inserted: false, Updated: false, "child-missing");

        if (parent.TargetId != message.TargetId || child.TargetId != message.TargetId)
            return new AssetRelationshipResult(null, Inserted: false, Updated: false, "cross-target");

        if (!relationshipValidator.IsAllowed(parent.Kind, child.Kind, message.RelationshipType))
            return new AssetRelationshipResult(null, Inserted: false, Updated: false, "rule-denied");

        var existing = await db.AssetRelationships
            .FirstOrDefaultAsync(
                r => r.TargetId == message.TargetId
                    && r.ParentAssetId == message.ParentAssetId
                    && r.ChildAssetId == message.ChildAssetId
                    && r.RelationshipType == message.RelationshipType,
                cancellationToken)
            .ConfigureAwait(false);

        var now = message.OccurredAtUtc == default ? DateTimeOffset.UtcNow : message.OccurredAtUtc;
        var isPrimary = message.IsPrimary && message.RelationshipType == AssetRelationshipType.Contains;

        if (existing is null
            && await relationshipValidator.WouldCreateCycleAsync(message.TargetId, message.ParentAssetId, message.ChildAssetId, cancellationToken)
                .ConfigureAwait(false))
        {
            return new AssetRelationshipResult(null, Inserted: false, Updated: false, "cycle");
        }

        if (isPrimary)
            await ClearPrimaryContainsRelationshipAsync(message.TargetId, message.ChildAssetId, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            existing.LastSeenAtUtc = now;
            existing.Confidence = ClampConfidence(message.Confidence);
            existing.DiscoveredBy = Truncate(message.DiscoveredBy, 128);
            existing.DiscoveryContext = Truncate(message.DiscoveryContext ?? "", 512);
            existing.PropertiesJson = NullIfWhiteSpace(message.PropertiesJson);
            existing.IsPrimary = existing.IsPrimary || isPrimary;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AssetRelationshipResult(existing.Id, Inserted: false, Updated: true);
        }

        var relationship = new AssetRelationship
        {
            Id = Guid.NewGuid(),
            TargetId = message.TargetId,
            ParentAssetId = message.ParentAssetId,
            ChildAssetId = message.ChildAssetId,
            RelationshipType = message.RelationshipType,
            IsPrimary = isPrimary,
            Confidence = ClampConfidence(message.Confidence),
            DiscoveredBy = Truncate(message.DiscoveredBy, 128),
            DiscoveryContext = Truncate(message.DiscoveryContext ?? "", 512),
            PropertiesJson = NullIfWhiteSpace(message.PropertiesJson),
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
        };

        db.AssetRelationships.Add(relationship);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AssetRelationshipResult(relationship.Id, Inserted: true, Updated: false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.Entry(relationship).State = EntityState.Detached;
            return await UpsertRelationshipInsideTransactionAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AssetNodeDto?> GetRootAssetAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        var root = await db.Assets.AsNoTracking()
            .Where(a => a.TargetId == targetId && a.Kind == AssetKind.Target)
            .OrderBy(a => a.DiscoveredAtUtc)
            .Select(a => ToNodeDto(a))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (root is not null)
            return root;

        var target = await db.Targets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == targetId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
            return null;

        var created = await EnsureRootAssetAsync(target, cancellationToken).ConfigureAwait(false);
        return ToNodeDto(created);
    }

    public Task<AssetNodeDto?> GetAssetAsync(Guid targetId, Guid assetId, CancellationToken cancellationToken = default) =>
        db.Assets.AsNoTracking()
            .Where(a => a.TargetId == targetId && a.Id == assetId)
            .Select(a => ToNodeDto(a))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AssetNodeDto>> GetChildrenAsync(
        Guid targetId,
        Guid parentAssetId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.AssetRelationships.AsNoTracking()
            .Where(r => r.TargetId == targetId && r.ParentAssetId == parentAssetId)
            .Join(
                db.Assets.AsNoTracking(),
                rel => rel.ChildAssetId,
                asset => asset.Id,
                (rel, asset) => new { rel, asset })
            .OrderByDescending(x => x.rel.IsPrimary)
            .ThenBy(x => x.asset.Kind)
            .ThenBy(x => x.asset.RawValue)
            .Select(x => ToNodeDto(x.asset))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }

    public async Task<IReadOnlyList<AssetNodeDto>> GetParentsAsync(
        Guid targetId,
        Guid childAssetId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.AssetRelationships.AsNoTracking()
            .Where(r => r.TargetId == targetId && r.ChildAssetId == childAssetId)
            .Join(
                db.Assets.AsNoTracking(),
                rel => rel.ParentAssetId,
                asset => asset.Id,
                (rel, asset) => new { rel, asset })
            .OrderByDescending(x => x.rel.IsPrimary)
            .ThenBy(x => x.asset.Kind)
            .ThenBy(x => x.asset.RawValue)
            .Select(x => ToNodeDto(x.asset))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }

    public async Task<IReadOnlyList<AssetNodeDto>> GetAncestorsAsync(
        Guid targetId,
        Guid childAssetId,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 50);
        var relationships = await db.AssetRelationships.AsNoTracking()
            .Where(r => r.TargetId == targetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ancestorIds = new List<Guid>();
        var seen = new HashSet<Guid> { childAssetId };
        var frontier = new List<Guid> { childAssetId };

        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = relationships
                .Where(r => frontier.Contains(r.ChildAssetId))
                .Select(r => r.ParentAssetId)
                .Where(seen.Add)
                .ToList();

            ancestorIds.AddRange(next);
            frontier = next;
        }

        if (ancestorIds.Count == 0)
            return Array.Empty<AssetNodeDto>();

        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.TargetId == targetId && ancestorIds.Contains(a.Id))
            .Select(a => ToNodeDto(a))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = assets.ToDictionary(a => a.Id);
        return ancestorIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public async Task<IReadOnlyList<AssetTreeNodeDto>> GetDescendantsAsync(
        Guid targetId,
        Guid rootAssetId,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 50);

        var root = await db.Assets.AsNoTracking()
            .Where(a => a.TargetId == targetId && a.Id == rootAssetId)
            .Select(a => ToNodeDto(a))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (root is null)
            return Array.Empty<AssetTreeNodeDto>();

        var relationships = await db.AssetRelationships.AsNoTracking()
            .Where(r => r.TargetId == targetId)
            .OrderByDescending(r => r.IsPrimary)
            .ThenBy(r => r.RelationshipType)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var reachableIds = new HashSet<Guid> { rootAssetId };
        var frontier = new List<Guid> { rootAssetId };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = relationships
                .Where(r => frontier.Contains(r.ParentAssetId))
                .Select(r => r.ChildAssetId)
                .Where(reachableIds.Add)
                .ToList();
            frontier = next;
        }

        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.TargetId == targetId && reachableIds.Contains(a.Id))
            .Select(a => ToNodeDto(a))
            .ToDictionaryAsync(a => a.Id, cancellationToken)
            .ConfigureAwait(false);

        AssetTreeNodeDto Build(Guid assetId, AssetRelationshipDto? incoming, int depth, HashSet<Guid> path)
        {
            if (!assets.TryGetValue(assetId, out var asset))
                throw new InvalidOperationException($"Asset {assetId} was reachable but not loaded.");

            if (depth >= maxDepth)
                return new AssetTreeNodeDto(asset, incoming, depth, Array.Empty<AssetTreeNodeDto>());

            path.Add(assetId);
            var children = relationships
                .Where(r => r.ParentAssetId == assetId && assets.ContainsKey(r.ChildAssetId) && !path.Contains(r.ChildAssetId))
                .OrderByDescending(r => r.IsPrimary)
                .ThenBy(r => assets[r.ChildAssetId].Kind)
                .ThenBy(r => assets[r.ChildAssetId].RawValue, StringComparer.OrdinalIgnoreCase)
                .Select(r => Build(r.ChildAssetId, ToRelationshipDto(r), depth + 1, new HashSet<Guid>(path)))
                .ToList();

            return new AssetTreeNodeDto(asset, incoming, depth, children);
        }

        return [Build(rootAssetId, null, 0, [])];
    }

    public async Task<StoredAsset> EnsureRootAssetAsync(ReconTarget target, CancellationToken cancellationToken = default)
    {
        var key = "target:" + target.RootDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var existing = await db.Assets
            .FirstOrDefaultAsync(a => a.TargetId == target.Id && a.CanonicalKey == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var now = DateTimeOffset.UtcNow;
        var root = new StoredAsset
        {
            Id = Guid.NewGuid(),
            TargetId = target.Id,
            Kind = AssetKind.Target,
            Category = AssetCategory.ScopeRoot,
            CanonicalKey = key,
            RawValue = target.RootDomain,
            DisplayName = target.RootDomain,
            Depth = 0,
            DiscoveredBy = "target-registration",
            DiscoveryContext = "Root target asset",
            DiscoveredAtUtc = target.CreatedAtUtc == default ? now : target.CreatedAtUtc,
            LastSeenAtUtc = now,
            Confidence = 1.0m,
            LifecycleStatus = AssetLifecycleStatus.Confirmed,
        };

        db.Assets.Add(root);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return root;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.Entry(root).State = EntityState.Detached;
            return await db.Assets
                .FirstAsync(a => a.TargetId == target.Id && a.CanonicalKey == key, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task TouchExistingAssetAsync(
        StoredAsset asset,
        DateTimeOffset now,
        AssetDiscovered message,
        CancellationToken cancellationToken)
    {
        asset.LastSeenAtUtc = now;
        asset.Confidence = 1.0m;
        if (asset.Category == default && asset.Kind != AssetKind.Target)
            asset.Category = AssetKindClassification.CategoryFor(asset.Kind);
        if (string.IsNullOrWhiteSpace(asset.DisplayName))
            asset.DisplayName = ResolveDisplayName(asset.Kind, asset.RawValue, message.RawValue);
        if (!string.IsNullOrWhiteSpace(message.DiscoveryContext))
            asset.DiscoveryContext = Truncate(message.DiscoveryContext, 512);

        await EnsureQueuedAssetHasHttpRequestAsync(asset, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearPrimaryContainsRelationshipAsync(Guid targetId, Guid childAssetId, CancellationToken cancellationToken)
    {
        await db.AssetRelationships
            .Where(r => r.TargetId == targetId
                && r.ChildAssetId == childAssetId
                && r.IsPrimary
                && r.RelationshipType == AssetRelationshipType.Contains)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsPrimary, false), cancellationToken)
            .ConfigureAwait(false);
    }

    private void DetachPendingAssetGraph(StoredAsset asset)
    {
        db.Entry(asset).State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<HttpRequestQueueItem>()
                     .Where(e => e.Entity.AssetId == asset.Id && e.State == EntityState.Added))
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task EnsureQueuedAssetHasHttpRequestAsync(StoredAsset asset, CancellationToken cancellationToken)
    {
        if (asset.LifecycleStatus != AssetLifecycleStatus.Queued || !ShouldRequest(asset.Kind))
            return;

        var hasQueueItem = await db.HttpRequestQueue.AsNoTracking()
            .AnyAsync(q => q.AssetId == asset.Id, cancellationToken)
            .ConfigureAwait(false);
        if (hasQueueItem)
            return;

        EnqueueHttpRequestIfNeeded(asset);
    }

    private void EnqueueHttpRequestIfNeeded(StoredAsset asset)
    {
        if (!ShouldRequest(asset.Kind))
            return;

        if (!TryResolveRequestUrl(asset, out var requestUrl, out var domainKey))
            return;

        db.HttpRequestQueue.Add(
            new HttpRequestQueueItem
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                TargetId = asset.TargetId,
                AssetKind = asset.Kind,
                Method = "GET",
                RequestUrl = requestUrl,
                DomainKey = domainKey,
                State = HttpRequestQueueState.Queued,
                Priority = ResolvePriority(asset.Kind),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = DateTimeOffset.UtcNow,
            });
    }

    private static bool ShouldRequest(AssetKind kind) =>
        kind is AssetKind.Url or AssetKind.ApiEndpoint or AssetKind.JavaScriptFile or AssetKind.MarkdownBody
            or AssetKind.Subdomain or AssetKind.Domain;

    private static int ResolvePriority(AssetKind kind) =>
        kind is AssetKind.Subdomain or AssetKind.Domain ? 10 : 0;

    private static bool TryResolveRequestUrl(StoredAsset asset, out string requestUrl, out string domainKey)
    {
        requestUrl = "";
        domainKey = "";

        if (asset.Kind is AssetKind.Subdomain or AssetKind.Domain)
        {
            var host = asset.RawValue.Trim().TrimEnd('/');
            if (host.Length == 0)
                return false;
            if (!Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var domainUri))
                return false;
            requestUrl = domainUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            domainKey = domainUri.IdnHost.ToLowerInvariant();
            return true;
        }

        var raw = asset.RawValue.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + raw, UriKind.Absolute, out uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        requestUrl = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        domainKey = uri.IdnHost.ToLowerInvariant();
        return true;
    }

    private static string ResolveInitialLifecycleStatus(AssetDiscovered message) =>
        message.Kind == AssetKind.Target || !ShouldRequest(message.Kind)
            ? AssetLifecycleStatus.Confirmed
            : AssetLifecycleStatus.Queued;

    private static string ResolveDisplayName(AssetKind kind, string normalizedDisplay, string rawValue) =>
        kind switch
        {
            AssetKind.Target => rawValue.Trim(),
            AssetKind.ApiMethod => rawValue.Trim(),
            AssetKind.Parameter => rawValue.Trim(),
            _ => string.IsNullOrWhiteSpace(normalizedDisplay) ? rawValue.Trim() : normalizedDisplay.Trim(),
        };

    private static AssetNodeDto ToNodeDto(StoredAsset a) =>
        new(
            a.Id,
            a.TargetId,
            a.Kind,
            a.Category,
            a.CanonicalKey,
            a.RawValue,
            a.LifecycleStatus,
            a.DisplayName,
            a.TypeDetailsJson);

    private static AssetRelationshipDto ToRelationshipDto(AssetRelationship r) =>
        new(
            r.Id,
            r.TargetId,
            r.ParentAssetId,
            r.ChildAssetId,
            r.RelationshipType,
            r.IsPrimary,
            r.Confidence,
            r.DiscoveredBy,
            r.DiscoveryContext,
            r.FirstSeenAtUtc,
            r.LastSeenAtUtc);

    private static decimal ClampConfidence(decimal confidence) => Math.Clamp(confidence, 0m, 1m);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string? s, int maxChars)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= maxChars ? s : s[..maxChars];
    }
}
