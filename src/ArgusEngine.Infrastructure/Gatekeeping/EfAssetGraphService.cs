using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Assets;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Persistence.Data;
using Npgsql;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class EfAssetGraphService(
    ArgusDbContext db,
    ILogger<EfAssetGraphService> logger) : IAssetGraphService
{
    private static readonly Action<ILogger, Guid, Exception?> LogSkipAssetGraphTargetMissing =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(1, nameof(LogSkipAssetGraphTargetMissing)),
            "Skip asset graph upsert: target {TargetId} not in recon_targets.");
    private static readonly Action<ILogger, Guid, Guid, Guid, string, Exception?> LogRejectedAssetRelationship =
        LoggerMessage.Define<Guid, Guid, Guid, string>(
            LogLevel.Debug,
            new EventId(2, nameof(LogRejectedAssetRelationship)),
            "Rejected asset relationship. TargetId={TargetId}, ParentAssetId={ParentAssetId}, ChildAssetId={ChildAssetId}, Reason={Reason}");

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
            var relResult = await UpsertRelationshipAsync(
                    message,
                    parentId,
                    child.Id,
                    message.RelationshipType,
                    message.IsPrimaryRelationship,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            relationshipInserted = relResult.Inserted;
            relationshipUpdated = relResult.Updated;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AssetUpsertResult(child.Id, inserted, relationshipInserted, relationshipUpdated);
    }

    private async Task<AssetRelationshipResult> UpsertRelationshipAsync(
        AssetDiscovered message,
        Guid parentAssetId,
        Guid childAssetId,
        AssetRelationshipType relationshipType,
        bool isPrimary,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.AssetRelationships
            .FirstOrDefaultAsync(
                r => r.TargetId == message.TargetId
                    && r.ParentAssetId == parentAssetId
                    && r.ChildAssetId == childAssetId
                    && r.RelationshipType == relationshipType,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.LastSeenAtUtc = now;
            existing.Confidence = ClampConfidence(message.Confidence);
            existing.DiscoveredBy = Truncate(message.DiscoveredBy, 128);
            existing.DiscoveryContext = Truncate(message.DiscoveryContext ?? "", 512);
            existing.PropertiesJson = NullIfWhiteSpace(message.PropertiesJson);
            if (isPrimary && !existing.IsPrimary)
            {
                if (relationshipType == AssetRelationshipType.Contains)
                    await ClearPrimaryContainsRelationshipAsync(message.TargetId, childAssetId, cancellationToken).ConfigureAwait(false);
                existing.IsPrimary = true;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AssetRelationshipResult(existing.Id, Inserted: false, Updated: true);
        }

        if (isPrimary && relationshipType == AssetRelationshipType.Contains)
            await ClearPrimaryContainsRelationshipAsync(message.TargetId, childAssetId, cancellationToken).ConfigureAwait(false);

        var relationship = new AssetRelationship
        {
            Id = Guid.NewGuid(),
            TargetId = message.TargetId,
            ParentAssetId = parentAssetId,
            ChildAssetId = childAssetId,
            RelationshipType = relationshipType,
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
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.Entry(relationship).State = EntityState.Detached;
            existing = await db.AssetRelationships
                .FirstAsync(
                    r => r.TargetId == message.TargetId
                        && r.ParentAssetId == parentAssetId
                        && r.ChildAssetId == childAssetId
                        && r.RelationshipType == relationshipType,
                    cancellationToken)
                .ConfigureAwait(false);
            existing.LastSeenAtUtc = now;
            existing.Confidence = ClampConfidence(message.Confidence);
            existing.DiscoveredBy = Truncate(message.DiscoveredBy, 128);
            existing.DiscoveryContext = Truncate(message.DiscoveryContext ?? "", 512);
            existing.PropertiesJson = NullIfWhiteSpace(message.PropertiesJson);
            existing.IsPrimary = existing.IsPrimary || isPrimary;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new AssetRelationshipResult(existing.Id, Inserted: false, Updated: true);
        }

        return new AssetRelationshipResult(relationship.Id, Inserted: true, Updated: false);
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

    private static decimal ClampConfidence(decimal confidence) => Math.Clamp(confidence, 0m, 1m);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string? s, int maxChars)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= maxChars ? s : s[..maxChars];
    }
}
