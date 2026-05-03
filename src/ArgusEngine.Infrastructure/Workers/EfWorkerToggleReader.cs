using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure.Persistence.Data;

namespace ArgusEngine.Infrastructure.Workers;

public sealed class EfWorkerToggleReader(ArgusDbContext db) : IWorkerToggleReader
{
    public async Task<bool> IsWorkerEnabledAsync(string workerKey, CancellationToken cancellationToken = default)
    {
        var row = await db.WorkerSwitches.AsNoTracking()
            .Where(w => w.WorkerKey == workerKey)
            .Select(w => (bool?)w.IsEnabled)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row ?? true;
    }
}
