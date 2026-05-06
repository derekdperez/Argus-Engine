using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Workers.Enumeration.Consumers;

public sealed class TargetCreatedConsumer(
    IWorkerToggleReader toggles,
    IEventOutbox outbox,
    IOptions<SubdomainEnumerationOptions> options,
    ILogger<TargetCreatedConsumer> logger) : IConsumer<TargetCreated>
{
    private static readonly Action<ILogger, string, Guid, Exception?> LogTriggeringProvider =
        LoggerMessage.Define<string, Guid>(
            LogLevel.Information,
            new EventId(1, nameof(LogTriggeringProvider)),
            "TargetCreated: triggering {Provider} enumeration for target {TargetId}");

    private static readonly Action<ILogger, Guid, Exception?> LogNoProviders =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(2, nameof(LogNoProviders)),
            "TargetCreated: no enumeration providers enabled for target {TargetId}");

    private static readonly Action<ILogger, Guid, Exception?> LogEnumerationDisabled =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(3, nameof(LogEnumerationDisabled)),
            "TargetCreated: enumeration worker disabled; skipping target {TargetId}");

    public async Task Consume(ConsumeContext<TargetCreated> context)
    {
        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.Enumeration, context.CancellationToken).ConfigureAwait(false))
        {
            LogEnumerationDisabled(logger, context.Message.TargetId, null);
            return;
        }

        var m = context.Message;
        var cfg = options.Value;
        if (!cfg.Enabled)
            return;

        var correlation = m.CorrelationId == Guid.Empty ? NewId.NextGuid() : m.CorrelationId;
        var causation = m.EventId == Guid.Empty ? correlation : m.EventId;
        var triggeredCount = 0;

        if (cfg.Subfinder.Enabled)
        {
            LogTriggeringProvider(logger, "subfinder", m.TargetId, null);
            await outbox.EnqueueAsync(
                new SubdomainEnumerationRequested(
                    m.TargetId,
                    m.RootDomain,
                    "subfinder",
                    "target-created",
                    DateTimeOffset.UtcNow,
                    correlation,
                    EventId: NewId.NextGuid(),
                    CausationId: causation,
                    Producer: "worker-enum"),
                context.CancellationToken).ConfigureAwait(false);
            triggeredCount++;
        }

        if (cfg.Amass.Enabled)
        {
            LogTriggeringProvider(logger, "amass", m.TargetId, null);
            await outbox.EnqueueAsync(
                new SubdomainEnumerationRequested(
                    m.TargetId,
                    m.RootDomain,
                    "amass",
                    "target-created",
                    DateTimeOffset.UtcNow,
                    correlation,
                    EventId: NewId.NextGuid(),
                    CausationId: causation,
                    Producer: "worker-enum"),
                context.CancellationToken).ConfigureAwait(false);
            triggeredCount++;
        }

        if (triggeredCount == 0)
        {
            LogNoProviders(logger, m.TargetId, null);
        }
    }
}
