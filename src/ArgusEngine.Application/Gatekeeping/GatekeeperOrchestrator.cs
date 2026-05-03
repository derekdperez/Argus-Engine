using MassTransit;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.Application.Gatekeeping;

/// <summary>
/// Asset Gatekeeper: Raw admissions are normalized, deduped, persisted, audited, then re-published as Indexed for workers.
/// </summary>
public sealed class GatekeeperOrchestrator(
    IAssetCanonicalizer canonicalizer,
    IAssetDeduplicator deduplicator,
    ITargetScopeEvaluator scope,
    IAssetPersistence persistence,
    IEventOutbox outbox,
    IWorkerToggleReader workerToggles,
    IAssetAdmissionDecisionWriter decisions,
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

    private static readonly Action<ILogger, string, Exception?> CanonicalizationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1005, nameof(CanonicalizationFailed)),
            "Canonicalization failed for raw asset {Raw}.");

    private static readonly Action<ILogger, string, string, Guid, string, Exception?> WriteDecisionFailed =
        LoggerMessage.Define<string, string, Guid, string>(
            LogLevel.Warning,
            new EventId(1006, nameof(WriteDecisionFailed)),
            "Unable to write asset admission decision {Decision}/{ReasonCode} for target {TargetId} raw {RawValue}.");

    public async Task ProcessAsync(AssetDiscovered message, CancellationToken cancellationToken = default)
    {
        if (message.AdmissionStage != AssetAdmissionStage.Raw)
            return;

        if (!await workerToggles.IsWorkerEnabledAsync(WorkerKeys.Gatekeeper, cancellationToken).ConfigureAwait(false))
        {
            await TryWriteDecisionAsync(
                Decision(
                    message,
                    null,
                    null,
                    AssetAdmissionDecisionKind.WorkerDisabled,
                    AssetAdmissionReasonCode.GatekeeperDisabled,
                    "Gatekeeper worker toggle is disabled."),
                cancellationToken).ConfigureAwait(false);

            LogGatekeeperDisabled(logger, message.RawValue);
            return;
        }

        if (message.Depth > message.GlobalMaxDepth)
        {
            await TryWriteDecisionAsync(
                Decision(
                    message,
                    null,
                    null,
                    AssetAdmissionDecisionKind.DepthExceeded,
                    AssetAdmissionReasonCode.MaxDepthExceeded,
                    $"Depth {message.Depth} exceeded max depth {message.GlobalMaxDepth}."),
                cancellationToken).ConfigureAwait(false);

            LogDroppingDepthExceeded(logger, message.RawValue, message.Depth, message.GlobalMaxDepth);
            return;
        }

        CanonicalAsset canonical;
        try
        {
            canonical = canonicalizer.Canonicalize(message);
        }
        catch (Exception ex)
        {
            await TryWriteDecisionAsync(
                Decision(
                    message,
                    null,
                    null,
                    AssetAdmissionDecisionKind.Invalid,
                    AssetAdmissionReasonCode.CanonicalizationFailed,
                    ex.Message),
                cancellationToken).ConfigureAwait(false);

            LogCanonicalizationFailed(logger, message.RawValue, ex);
            return;
        }

        var hasRelationshipContext = message.ParentAssetId is { } parentAssetId && parentAssetId != Guid.Empty;
        var reserved = await deduplicator.TryReserveAsync(message.TargetId, canonical.CanonicalKey, cancellationToken)
            .ConfigureAwait(false);

        if (!reserved && !hasRelationshipContext)
        {
            await TryWriteDecisionAsync(
                Decision(
                    message,
                    null,
                    canonical.CanonicalKey,
                    AssetAdmissionDecisionKind.Duplicate,
                    AssetAdmissionReasonCode.DuplicateCanonicalKey,
                    "Canonical key already reserved or persisted."),
                cancellationToken).ConfigureAwait(false);

            LogDedupeHit(logger, canonical.CanonicalKey);
            return;
        }

        try
        {
            if (!scope.IsInScope(message, canonical))
            {
                await TryWriteDecisionAsync(
                    Decision(
                        message,
                        null,
                        canonical.CanonicalKey,
                        AssetAdmissionDecisionKind.OutOfScope,
                        AssetAdmissionReasonCode.ScopeRejected,
                        "Scope evaluator rejected this asset."),
                    cancellationToken).ConfigureAwait(false);

                LogOutOfScope(logger, canonical.CanonicalKey);

                if (reserved)
                    await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken)
                        .ConfigureAwait(false);

                return;
            }

            var (assetId, inserted) = await persistence.PersistNewAssetAsync(message, canonical, cancellationToken)
                .ConfigureAwait(false);

            if (assetId == Guid.Empty)
            {
                await TryWriteDecisionAsync(
                    Decision(
                        message,
                        null,
                        canonical.CanonicalKey,
                        AssetAdmissionDecisionKind.PersistenceSkipped,
                        AssetAdmissionReasonCode.PersistenceReturnedEmptyAssetId,
                        "Persistence returned empty asset id."),
                    cancellationToken).ConfigureAwait(false);

                if (reserved)
                    await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken)
                        .ConfigureAwait(false);

                return;
            }

            if (!inserted)
            {
                await TryWriteDecisionAsync(
                    Decision(
                        message,
                        assetId,
                        canonical.CanonicalKey,
                        AssetAdmissionDecisionKind.Duplicate,
                        hasRelationshipContext
                            ? AssetAdmissionReasonCode.DuplicateWithRelationshipOnly
                            : AssetAdmissionReasonCode.DuplicateCanonicalKey,
                        hasRelationshipContext
                            ? "Existing asset updated only with relationship context; duplicate Indexed event suppressed."
                            : "Existing asset found during persistence; duplicate Indexed event suppressed."),
                    cancellationToken).ConfigureAwait(false);

                // Existing assets may still receive new graph edges. Do not publish duplicate Indexed events.
                if (reserved)
                    await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken)
                        .ConfigureAwait(false);

                return;
            }

            await TryWriteDecisionAsync(
                Decision(
                    message,
                    assetId,
                    canonical.CanonicalKey,
                    AssetAdmissionDecisionKind.Accepted,
                    AssetAdmissionReasonCode.AcceptedNewAsset,
                    "New asset accepted and indexed."),
                cancellationToken).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            await TryWriteDecisionAsync(
                Decision(
                    message,
                    null,
                    canonical.CanonicalKey,
                    AssetAdmissionDecisionKind.Failed,
                    AssetAdmissionReasonCode.ExceptionDuringAdmission,
                    ex.Message),
                cancellationToken).ConfigureAwait(false);

            if (reserved)
                await deduplicator.ReleaseAsync(message.TargetId, canonical.CanonicalKey, cancellationToken)
                    .ConfigureAwait(false);

            throw;
        }
    }

    private async Task TryWriteDecisionAsync(AssetAdmissionDecisionInput decision, CancellationToken cancellationToken)
    {
        try
        {
            await decisions.WriteAsync(decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWriteDecisionFailed(
                logger,
                decision.Decision,
                decision.ReasonCode,
                decision.TargetId,
                decision.RawValue,
                ex);
        }
    }

    private static AssetAdmissionDecisionInput Decision(
        AssetDiscovered message,
        Guid? assetId,
        string? canonicalKey,
        string decision,
        string reasonCode,
        string? reasonDetail)
    {
        return new AssetAdmissionDecisionInput(
            message.TargetId,
            assetId,
            message.RawValue,
            canonicalKey,
            message.Kind.ToString(),
            decision,
            reasonCode,
            reasonDetail,
            message.DiscoveredBy,
            message.DiscoveryContext,
            message.Depth,
            message.GlobalMaxDepth,
            message.CorrelationId,
            message.CausationId == Guid.Empty ? null : message.CausationId,
            message.EventId == Guid.Empty ? null : message.EventId);
    }

    private static void LogGatekeeperDisabled(ILogger logger, string raw) =>
        GatekeeperDisabled(logger, raw, null);

    private static void LogDroppingDepthExceeded(ILogger logger, string raw, int depth, int maxDepth) =>
        DroppingDepthExceeded(logger, raw, depth, maxDepth, null);

    private static void LogDedupeHit(ILogger logger, string key) =>
        DedupeHit(logger, key, null);

    private static void LogOutOfScope(ILogger logger, string key) =>
        OutOfScope(logger, key, null);

    private static void LogCanonicalizationFailed(ILogger logger, string raw, Exception ex) =>
        CanonicalizationFailed(logger, raw, ex);

    private static void LogWriteDecisionFailed(ILogger logger, string kind, string reason, Guid targetId, string raw, Exception ex) =>
        WriteDecisionFailed(logger, kind, reason, targetId, raw, ex);

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
