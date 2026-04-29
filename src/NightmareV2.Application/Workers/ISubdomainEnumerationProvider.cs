using NightmareV2.Contracts.Events;

namespace NightmareV2.Application.Workers;

public interface ISubdomainEnumerationProvider
{
    string Name { get; }

    Task<IReadOnlyCollection<SubdomainEnumerationResult>> EnumerateAsync(
        SubdomainEnumerationRequested request,
        CancellationToken cancellationToken = default);
}
