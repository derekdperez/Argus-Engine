using MassTransit;
using Microsoft.Extensions.Options;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Workers.Enum.Consumers;

public sealed class SubdomainEnumerationRequestedConsumer(
    ILogger<SubdomainEnumerationRequestedConsumer> logger,
    IEnumerable<ISubdomainEnumerationProvider> providers,
    IWorkerToggleReader toggles,
    IEventOutbox outbox,
    ITargetLookup targetLookup,
    IAssetGraphService graph,
    IOptions<SubdomainEnumerationOptions> options) : IConsumer<SubdomainEnumerationRequested>
{
    public async Task Consume(ConsumeContext<SubdomainEnumerationRequested> context)
    {
        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.Enumeration, context.CancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Enumeration disabled; skipping provider job {Provider} for {RootDomain}.", context.Message.Provider, context.Message.RootDomain);
            return;
        }

        var message = context.Message;
        var cfg = options.Value;
        if (!cfg.Enabled)
            return;

        var target = await targetLookup.FindAsync(message.TargetId, context.CancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            logger.LogWarning(
                "Enumeration request rejected: target does not exist. Provider={Provider}, TargetId={TargetId}, RootDomain={RootDomain}",
                message.Provider,
                message.TargetId,
                message.RootDomain);
            return;
        }

        if (!string.Equals(target.RootDomain, message.RootDomain, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Enumeration request rejected: root domain does not match target. Provider={Provider}, TargetId={TargetId}, RequestedRoot={RequestedRoot}, ActualRoot={ActualRoot}",
                message.Provider,
                message.TargetId,
                message.RootDomain,
                target.RootDomain);
            return;
        }

        var provider = providers.FirstOrDefault(
            x => string.Equals(x.Name, message.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogWarning("No subdomain enumeration provider registered for {Provider}", message.Provider);
            return;
        }

        logger.LogInformation(
            "Starting subdomain enumeration. Provider={Provider}, TargetId={TargetId}, RootDomain={RootDomain}",
            message.Provider,
            message.TargetId,
            message.RootDomain);

        IReadOnlyCollection<SubdomainEnumerationResult> rawResults;
        try
        {
            rawResults = await provider.EnumerateAsync(message, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Subdomain enumeration provider failed. Provider={Provider}, RootDomain={RootDomain}",
                message.Provider,
                message.RootDomain);
            return;
        }

        var emittedCount = 0;
        var rejectedNormalizationCount = 0;
        var rejectedScopeCount = 0;
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxPerJob = Math.Clamp(cfg.MaxSubdomainsPerJob, 1, 1_000_000);
        var correlation = message.CorrelationId == Guid.Empty ? NewId.NextGuid() : message.CorrelationId;
        var causation = message.EventId == Guid.Empty ? correlation : message.EventId;
        var rootAsset = await graph.GetRootAssetAsync(message.TargetId, context.CancellationToken).ConfigureAwait(false);

        foreach (var raw in rawResults)
        {
            var normalized = SubdomainEnumerationNormalization.NormalizeHostname(raw.Hostname);
            if (normalized is null || !SubdomainEnumerationNormalization.IsValidHostname(normalized))
            {
                rejectedNormalizationCount++;
                continue;
            }

            if (!SubdomainEnumerationNormalization.IsInScope(normalized, target.RootDomain))
            {
                rejectedScopeCount++;
                continue;
            }

            if (!dedupe.Add(normalized))
                continue;

            await outbox.EnqueueAsync(
                    new AssetDiscovered(
                        message.TargetId,
                        target.RootDomain,
                        target.GlobalMaxDepth,
                        Depth: 1,
                        Kind: AssetKind.Subdomain,
                        RawValue: normalized,
                        DiscoveredBy: $"enum-worker:{raw.Provider}",
                        OccurredAt: DateTimeOffset.UtcNow,
                        CorrelationId: correlation,
                        AdmissionStage: AssetAdmissionStage.Raw,
                        AssetId: null,
                        DiscoveryContext: $"Subdomain enumeration provider={raw.Provider}; method={raw.Method}",
                        ParentAssetId: rootAsset?.Id,
                        RelationshipType: AssetRelationshipType.Contains,
                        IsPrimaryRelationship: true,
                        EventId: NewId.NextGuid(),
                        CausationId: causation,
                        Producer: "worker-enum"),
                    context.CancellationToken)
                .ConfigureAwait(false);
            emittedCount++;
            if (emittedCount >= maxPerJob)
                break;
        }

        logger.LogInformation(
            "Completed subdomain enumeration. Provider={Provider}, RootDomain={RootDomain}, RawResults={RawCount}, EmittedResults={EmittedCount}, RejectedNormalization={RejectedNormalization}, RejectedOutOfScope={RejectedScope}, DeduplicatedWithinJob={DedupedCount}",
            message.Provider,
            target.RootDomain,
            rawResults.Count,
            emittedCount,
            rejectedNormalizationCount,
            rejectedScopeCount,
            rawResults.Count - rejectedNormalizationCount - rejectedScopeCount - emittedCount);
    }

}
