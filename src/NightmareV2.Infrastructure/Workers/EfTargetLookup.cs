using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.Workers;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.Infrastructure.Workers;

public sealed class EfTargetLookup(IDbContextFactory<NightmareDbContext> dbFactory) : ITargetLookup
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
