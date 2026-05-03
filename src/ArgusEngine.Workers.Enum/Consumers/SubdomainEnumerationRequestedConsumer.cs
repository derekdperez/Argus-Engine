using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Workers.Enum.Consumers;

public sealed class SubdomainEnumerationRequestedConsumer(
    ILogger<SubdomainEnumerationRequestedConsumer> logger,
    IEnumerable<ISubdomainEnumerationProvider> providers,
    IWorkerToggleReader toggles,
    IEventOutbox outbox,
    ITargetLookup targetLookup,
    IAssetGraphService graph,
    IOptions<SubdomainEnumerationOptions> options) : IConsumer<SubdomainEnumerationRequested>
{
    private static readonly Action<ILogger, string, string, Exception?> LogEnumerationDisabled =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(1, nameof(LogEnumerationDisabled)),
            "Enumeration disabled; skipping provider job {Provider} for {RootDomain}.");

    private static readonly Action<ILogger, string, Guid, string, Exception?> LogTargetMissing =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogTargetMissing)),
            "Enumeration request rejected: target does not exist. Provider={Provider}, TargetId={TargetId}, RootDomain={RootDomain}");

    private static readonly Action<ILogger, string, Guid, string, string, Exception?> LogDomainMismatch =
        LoggerMessage.Define<string, Guid, string, string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogDomainMismatch)),
            "Enumeration request rejected: root domain does not match target. Provider={Provider}, TargetId={TargetId}, RequestedRoot={RequestedRoot}, ActualRoot={ActualRoot}");

    private static readonly Action<ILogger, string, Exception?> LogNoProvider =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogNoProvider)),
            "No subdomain enumeration provider registered for {Provider}");

    private static readonly Action<ILogger, string, Guid, string, Exception?> LogEnumerationStarted =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Information,
            new EventId(5, nameof(LogEnumerationStarted)),
            "Starting subdomain enumeration. Provider={Provider}, TargetId={TargetId}, RootDomain={RootDomain}");

    private static readonly Action<ILogger, string, string, Exception?> LogProviderFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(6, nameof(LogProviderFailed)),
            "Subdomain enumeration provider failed. Provider={Provider}, RootDomain={RootDomain}");

    private static readonly Action<ILogger, string, string, int, int, int, int, Exception?> LogEnumerationCompleted =
        LoggerMessage.Define<string, string, int, int, int, int>(
            LogLevel.Information,
            new EventId(7, nameof(LogEnumerationCompleted)),
            "Completed subdomain enumeration. Provider={Provider}, RootDomain={RootDomain}, RawResults={RawCount}, EmittedResults={EmittedCount}, RejectedNormalization={RejectedNormalization}, RejectedOutOfScope={RejectedScope}");

    public async Task Consume(ConsumeContext<SubdomainEnumerationRequested> context)
    {
        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.Enumeration, context.CancellationToken).ConfigureAwait(false))
        {
            LogEnumerationDisabled(logger, context.Message.Provider, context.Message.RootDomain, null);
            return;
        }

        var message = context.Message;
        var cfg = options.Value;
        if (!cfg.Enabled)
            return;

        var target = await targetLookup.FindAsync(message.TargetId, context.CancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            LogTargetMissing(logger, message.Provider, message.TargetId, message.RootDomain, null);
            return;
        }

        if (!string.Equals(target.RootDomain, message.RootDomain, StringComparison.OrdinalIgnoreCase))
        {
            LogDomainMismatch(logger, message.Provider, message.TargetId, message.RootDomain, target.RootDomain, null);
            return;
        }

        var provider = providers.FirstOrDefault(
            x => string.Equals(x.Name, message.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            LogNoProvider(logger, message.Provider, null);
            return;
        }

        LogEnumerationStarted(logger, message.Provider, message.TargetId, message.RootDomain, null);

        IReadOnlyCollection<SubdomainEnumerationResult> rawResults;
        try
        {
            rawResults = await provider.EnumerateAsync(message, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogProviderFailed(logger, message.Provider, message.RootDomain, ex);
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

        LogEnumerationCompleted(
            logger,
            message.Provider,
            target.RootDomain,
            rawResults.Count,
            emittedCount,
            rejectedNormalizationCount,
            rejectedScopeCount,
            null);
    }
}
