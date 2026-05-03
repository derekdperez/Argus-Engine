namespace ArgusEngine.Application.Workers;

public interface ITargetLookup
{
    Task<TargetLookupResult?> FindAsync(Guid targetId, CancellationToken cancellationToken = default);
}

public sealed record TargetLookupResult(Guid TargetId, string RootDomain, int GlobalMaxDepth);
