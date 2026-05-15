using Microsoft.Extensions.Logging;
using MassTransit;
using ArgusEngine.Application.Orchestration;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Workers.Enumeration.Consumers;

public sealed class TargetCreatedConsumer(
    IWorkerToggleReader toggles,
    IReconOrchestrator reconOrchestrator,
    ILogger<TargetCreatedConsumer> logger) : IConsumer<TargetCreated>
{
    private static readonly Action<ILogger, Guid, string, Exception?> LogAttaching =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogAttaching)),
            "TargetCreated: attaching ReconOrchestrator to target {TargetId} ({RootDomain}).");

    private static readonly Action<ILogger, Guid, bool, int, int, int, bool, Exception?> LogTickCompleted =
        LoggerMessage.Define<Guid, bool, int, int, int, bool>(
            LogLevel.Information,
            new EventId(2, nameof(LogTickCompleted)),
            "TargetCreated: ReconOrchestrator tick finished for target {TargetId}. Claimed={Claimed}, ProvidersQueued={ProvidersQueued}, SubdomainsChecked={SubdomainsChecked}, SeedsQueued={SeedsQueued}, Completed={Completed}");

    private static readonly Action<ILogger, Guid, Exception?> LogEnumerationDisabled =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(3, nameof(LogEnumerationDisabled)),
            "TargetCreated: enumeration worker disabled; skipping ReconOrchestrator attach for target {TargetId}.");

    public async Task Consume(ConsumeContext<TargetCreated> context)
    {
        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.Enumeration, context.CancellationToken).ConfigureAwait(false))
        {
            LogEnumerationDisabled(logger, context.Message.TargetId, null);
            return;
        }

        var m = context.Message;
        LogAttaching(logger, m.TargetId, m.RootDomain, null);

        await reconOrchestrator.AttachToTargetAsync(m.TargetId, "target-created", context.CancellationToken)
            .ConfigureAwait(false);

        var result = await reconOrchestrator.TickTargetAsync(
                m.TargetId,
                $"target-created-{Environment.MachineName}",
                context.CancellationToken)
            .ConfigureAwait(false);

        LogTickCompleted(
            logger,
            m.TargetId,
            result.Claimed,
            result.ProvidersQueued,
            result.SubdomainsChecked,
            result.SubdomainSeedsQueued,
            result.Completed,
            null);
    }
}
