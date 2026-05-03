using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Application.Workers;

public interface ISubdomainEnumerationProvider
{
    string Name { get; }

    Task<IReadOnlyCollection<SubdomainEnumerationResult>> EnumerateAsync(
        SubdomainEnumerationRequested request,
        CancellationToken cancellationToken = default);
}
