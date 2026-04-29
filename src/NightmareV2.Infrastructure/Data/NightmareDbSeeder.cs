using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.Workers;
using NightmareV2.Domain.Entities;

namespace NightmareV2.Infrastructure.Data;

public static class NightmareDbSeeder
{
    public static async Task SeedWorkerSwitchesAsync(NightmareDbContext db, CancellationToken cancellationToken = default)
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
