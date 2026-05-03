using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.Workers;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Persistence.Data;

namespace ArgusEngine.Infrastructure.Persistence.Data;

public static class ArgusDbSeeder
{
    public static async Task SeedWorkerSwitchesAsync(ArgusDbContext db, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await db.WorkerSwitches
            .Select(w => w.WorkerKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var required = new[]
        {
            WorkerKeys.Gatekeeper,
            WorkerKeys.Spider,
            WorkerKeys.Enumeration,
            WorkerKeys.PortScan,
            WorkerKeys.HighValueRegex,
            WorkerKeys.HighValuePaths,
            WorkerKeys.TechnologyIdentification,
        };
        foreach (var key in required)
        {
            if (existing.Contains(key))
                continue;
            db.WorkerSwitches.Add(new WorkerSwitch { WorkerKey = key, IsEnabled = true, UpdatedAtUtc = now });
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
