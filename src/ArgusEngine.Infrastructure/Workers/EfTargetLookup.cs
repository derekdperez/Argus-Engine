using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.Workers;

public sealed class EfTargetLookup(IDbContextFactory<ArgusDbContext> dbFactory) : ITargetLookup
{
    public async Task<TargetLookupResult?> FindAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Targets.AsNoTracking()
            .Where(t => t.Id == targetId)
            .Select(t => new TargetLookupResult(t.Id, t.RootDomain, t.GlobalMaxDepth))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
