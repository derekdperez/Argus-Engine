using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Workers;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using System.Diagnostics;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed partial class WorkerHeartbeatService(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<ArgusDbContext> dbFactory,
    string workerKey,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private readonly string _hostName = Environment.MachineName;
    private readonly int _pid = Environment.ProcessId;

    [LoggerMessage(Level = LogLevel.Information, Message = "WorkerHeartbeatService started for {WorkerKey} on {HostName}.")]
    private partial void LogServiceStarted(string workerKey, string hostName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send worker heartbeat.")]
    private partial void LogHeartbeatFailed(Exception ex);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(workerKey, _hostName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogHeartbeatFailed(ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var healthCheck = scope.ServiceProvider.GetService<IWorkerHealthCheck>();
        
        bool isHealthy = true;
        string? message = null;

        if (healthCheck != null)
        {
            try
            {
                var result = await healthCheck.RunAsync(ct).ConfigureAwait(false);
                isHealthy = result.Success;
                message = result.Message;
            }
            catch (Exception ex)
            {
                isHealthy = false;
                message = $"Health check failed: {ex.Message}";
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.WorkerHeartbeats
            .FirstOrDefaultAsync(h => h.HostName == _hostName && h.WorkerKey == workerKey, ct)
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
        existing.IsHealthy = isHealthy;
        existing.HealthMessage = message;
        
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
