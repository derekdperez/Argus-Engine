using MassTransit;
using NightmareV2.Application.Events;
using Microsoft.Extensions.Logging;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Application.Gatekeeping;

/// <summary>
/// Asset Gatekeeper: Raw admissions are normalized, deduped, persisted, then re-published as Indexed for workers.
/// </summary>
public sealed class GatekeeperOrchestrator(
    IAssetCanonicalizer canonicalizer,
    IAssetDeduplicator deduplicator,
    ITargetScopeEvaluator scope,
    IAssetPersistence persistence,
    IEventOutbox outbox,
    IWorkerToggleReader workerToggles,
    ILogger<GatekeeperOrchestrator> logger)
{
    private static readonly Action<ILogger, string, Exception?> GatekeeperDisabled =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(1001, nameof(GatekeeperDisabled)),
            "Gatekeeper disabled; skipping Raw {Raw}");

    private static readonly Action<ILogger, string, int, int, Exception?> DroppingDepthExceeded =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Debug,
            new EventId(1002, nameof(DroppingDepthExceeded)),
            "Dropping {Raw} depth {Depth} > max {Max}");

    private static readonly Action<ILogger, string, Exception?> DedupeHit =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(1003, nameof(DedupeHit)),
            "Dedupe hit for {Key}");

    private static readonly Action<ILogger, string, Exception?> OutOfScope =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1004, nameof(OutOfScope)),
            "Out of scope: {Key}");

    public async Task ProcessAsync(AssetDiscovered message, CancellationToken cancellationToken = default)
    {
        if (message.AdmissionStage != AssetAdmissionStage.Raw)
            return;

        if (!await workerToggles.IsWorkerEnabledAsync(WorkerKeys.Gatekeeper, cancellationToken).ConfigureAwait(false))
        {
            LogGatekeeperDisabled(logger, message.RawValue);
            return;
        }

        if (message.Depth > message.GlobalMaxDepth)
        {
            LogDroppingDepthExceeded(logger, message.RawValue, message.Depth, message.GlobalMaxDepth);
            return;
        }

        var canonical = canonicalizer.Canonicalize(message);
        var hasRelationshipContext = message.ParentAssetId is { } parentAssetId && parentAssetId != Guid.Empty;
        var reserved = await deduplicator.TryReserveAsync(message.TargetId, canonical.CanonicalKey, cancellationToken)
            .ConfigureAwait(false);
        if (!reserved && !hasRelationshipContext)
        {
            LogDedupeHit(logger, canonical.CanonicalKey);
            return;
        }

        try
        {
            if (!scope.IsInScope(message, canonical))
            {
                LogOutOfScope(logger, canonical.CanonicalKey);
                if (reserved)
                    await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            var (assetId, inserted) = await persistence.PersistNewAssetAsync(message, canonical, cancellationToken).ConfigureAwait(false);
            if (assetId == Guid.Empty)
            {
                if (reserved)
                    await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!inserted)
            {
                // Existing assets may still receive new graph edges. Do not publish duplicate Indexed events.
                if (reserved)
                    await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            await PublishIndexedAsync(message, canonical, assetId, cancellationToken).ConfigureAwait(false);

            if (message.Kind == AssetKind.IpAddress
                && await workerToggles.IsWorkerEnabledAsync(WorkerKeys.PortScan, cancellationToken).ConfigureAwait(false))
            {
                var causation = message.EventId == Guid.Empty ? message.CorrelationId : message.EventId;
                await outbox.EnqueueAsync(
                        new PortScanRequested(
                            message.TargetId,
                            message.TargetRootDomain,
                            message.GlobalMaxDepth,
                            message.Depth,
                            canonical.NormalizedDisplay,
                            assetId,
                            message.CorrelationId,
                            EventId: NewId.NextGuid(),
                            CausationId: causation,
                            OccurredAtUtc: DateTimeOffset.UtcNow,
                            Producer: "gatekeeper"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            if (reserved)
                await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void LogGatekeeperDisabled(ILogger logger, string raw) =>
        GatekeeperDisabled(logger, raw, null);

    private static void LogDroppingDepthExceeded(ILogger logger, string raw, int depth, int maxDepth) =>
        DroppingDepthExceeded(logger, raw, depth, maxDepth, null);

    private static void LogDedupeHit(ILogger logger, string key) =>
        DedupeHit(logger, key, null);

    private static void LogOutOfScope(ILogger logger, string key) =>
        OutOfScope(logger, key, null);

    private Task PublishIndexedAsync(
        AssetDiscovered message,
        CanonicalAsset canonical,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var rawForWorkers = canonical.NormalizedDisplay;
        if (message.Kind is AssetKind.Subdomain or AssetKind.Domain)
            rawForWorkers = canonical.NormalizedDisplay.Trim().TrimEnd('/');
        var causation = message.EventId == Guid.Empty ? message.CorrelationId : message.EventId;

        return outbox.EnqueueAsync(
            new AssetDiscovered(
                message.TargetId,
                message.TargetRootDomain,
                message.GlobalMaxDepth,
                message.Depth,
                message.Kind,
                rawForWorkers,
                "gatekeeper",
                DateTimeOffset.UtcNow,
                message.CorrelationId,
                AssetAdmissionStage.Indexed,
                assetId,
                message.DiscoveryContext,
                message.ParentAssetId,
                message.RelationshipType,
                message.IsPrimaryRelationship,
                message.RelationshipPropertiesJson,
                EventId: NewId.NextGuid(),
                CausationId: causation,
                Producer: "gatekeeper"),
            cancellationToken);
    }
}
