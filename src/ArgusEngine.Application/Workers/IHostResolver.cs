namespace ArgusEngine.Application.Workers;

public interface IHostResolver
{
    Task<IReadOnlyCollection<string>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default);
}
