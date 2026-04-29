using MassTransit;
using Microsoft.Extensions.Options;
using NightmareV2.Application.Events;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Workers.Enum.Consumers;

public sealed class TargetCreatedConsumer(
    ILogger<TargetCreatedConsumer> logger,
    IWorkerToggleReader toggles,
    IInboxDeduplicator inbox,
    IEventOutbox outbox,
    IOptions<SubdomainEnumerationOptions> options) : IConsumer<TargetCreated>
{
    public async Task Consume(ConsumeContext<TargetCreated> context)
    {
        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(TargetCreatedConsumer), context.CancellationToken).ConfigureAwait(false))
            return;

        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.Enumeration, context.CancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Enumeration disabled; skipping target {TargetId}", context.Message.TargetId);
            return;
        }

        var cfg = options.Value;
        var message = context.Message;
        logger.LogInformation(
            "TargetCreated consumed. TargetId={TargetId}, RootDomain={RootDomain}",
            message.TargetId,
            message.RootDomain);

        if (!cfg.Enabled || !cfg.QueueProvidersOnTargetCreated)
        {
            logger.LogInformation(
                "Subdomain enumeration queueing disabled. TargetId={TargetId}, RootDomain={RootDomain}",
                message.TargetId,
                message.RootDomain);
            return;
        }

        var enabledProviders = ResolveEnabledProviders(cfg).ToList();
        var queuedProviders = new List<string>(capacity: enabledProviders.Count);

        var correlation = message.CorrelationId == Guid.Empty ? NewId.NextGuid() : message.CorrelationId;
        var causation = message.EventId == Guid.Empty ? correlation : message.EventId;
        foreach (var provider in enabledProviders)
        {
            var requested = new SubdomainEnumerationRequested(
                message.TargetId,
                message.RootDomain,
                provider,
                RequestedBy: "target-created-consumer",
                RequestedAt: DateTimeOffset.UtcNow,
                CorrelationId: correlation,
                EventId: NewId.NextGuid(),
                CausationId: causation,
                Producer: "worker-enum");

            await outbox.EnqueueAsync(requested, context.CancellationToken).ConfigureAwait(false);
            queuedProviders.Add(provider);
            logger.LogInformation(
                "Queued subdomain enumeration job. Provider={Provider}, TargetId={TargetId}, RootDomain={RootDomain}",
                provider,
                message.TargetId,
                message.RootDomain);
        }

        logger.LogInformation(
            "Queued {Count} subdomain enumeration jobs for {RootDomain}: {Providers}",
            queuedProviders.Count,
            message.RootDomain,
            string.Join(",", queuedProviders));
    }

    private static IEnumerable<string> ResolveEnabledProviders(SubdomainEnumerationOptions options)
    {
        foreach (var provider in options.DefaultProviders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (provider.Equals("subfinder", StringComparison.OrdinalIgnoreCase) && options.Subfinder.Enabled)
                yield return "subfinder";
            else if (provider.Equals("amass", StringComparison.OrdinalIgnoreCase) && options.Amass.Enabled)
                yield return "amass";
        }
    }
}
