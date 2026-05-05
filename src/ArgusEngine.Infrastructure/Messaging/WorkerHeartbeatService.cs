using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using System.Diagnostics;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class WorkerHeartbeatService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ArgusDbContext> dbFactory,
    string workerKey,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private readonly string _hostName = Environment.MachineName;
    private readonly int _pid = Process.GetCurrentProcess().Id;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WorkerHeartbeatService started for {WorkerKey} on {HostName}.", workerKey, _hostName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send worker heartbeat.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.WorkerHeartbeats
            .FirstOrDefaultAsync(h => h.HostName == _hostName, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new WorkerHeartbeat
            {
                HostName = _hostName,
                WorkerKey = workerKey,
                ProcessId = _pid
            };
            db.WorkerHeartbeats.Add(existing);
        }

        existing.LastHeartbeatUtc = DateTimeOffset.UtcNow;
        existing.WorkerKey = workerKey;
        existing.ProcessId = _pid;
        // active count could be tracked via an Interlocked counter in BusJournalObservers if we wanted more accuracy
        
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
