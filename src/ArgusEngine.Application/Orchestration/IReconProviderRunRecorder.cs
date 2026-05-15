namespace ArgusEngine.Application.Orchestration;

public interface IReconProviderRunRecorder
{
    Task MarkProviderStartedAsync(
        Guid targetId,
        string provider,
        Guid correlationId,
        Guid eventId,
        CancellationToken cancellationToken = default);

    Task MarkProviderAwaitingAssetPersistenceAsync(
        Guid targetId,
        string provider,
        IReadOnlyCollection<string> emittedSubdomainKeys,
        CancellationToken cancellationToken = default);

    Task MarkProviderCompletedAsync(
        Guid targetId,
        string provider,
        int emittedSubdomainCount,
        CancellationToken cancellationToken = default);

    Task MarkProviderSkippedAsync(
        Guid targetId,
        string provider,
        string reason,
        CancellationToken cancellationToken = default);

    Task MarkProviderFailedAsync(
        Guid targetId,
        string provider,
        string error,
        CancellationToken cancellationToken = default);
}
