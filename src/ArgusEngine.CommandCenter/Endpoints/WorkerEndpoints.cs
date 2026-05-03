using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/workers/activity",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var snapshot = await WorkerActivityQuery.BuildSnapshotAsync(db, ct).ConfigureAwait(false);
                    return Results.Ok(snapshot);
                })
            .WithName("GetWorkerActivity");

        app.MapGet(
                "/api/workers",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var switches = await db.WorkerSwitches.AsNoTracking()
                        .OrderBy(s => s.WorkerKey)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(switches);
                })
            .WithName("GetWorkerSwitches");

        return app;
    }
}
