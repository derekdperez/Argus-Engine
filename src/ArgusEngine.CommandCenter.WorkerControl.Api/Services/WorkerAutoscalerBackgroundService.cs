using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Services;

/// <summary>
/// Background service that automatically scales worker services based on queue backlog.
/// This provides built-in autoscaling without requiring external cron jobs or scripts.
/// </summary>
public sealed class WorkerAutoscalerBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<WorkerAutoscalerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan ScaleCheckInterval = TimeSpan.FromSeconds(Random.Shared.Next(28, 32));
    private static readonly TimeSpan ScaleUpCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ScaleDownCooldown = TimeSpan.FromSeconds(60);

    private static readonly (string ServiceName, string WorkerKey, string QueueSource, int DefaultTargetBacklog)[] WorkerDefinitions =
    [
        ("worker-spider", WorkerKeys.Spider, "http-queue", 100),
        ("worker-http-requester", WorkerKeys.HttpRequester, "http-queue", 100),
        ("worker-enum", WorkerKeys.Enumeration, "rabbitmq", 25),
        ("worker-portscan", WorkerKeys.PortScan, "rabbitmq", 100),
        ("worker-highvalue", WorkerKeys.HighValueRegex, "rabbitmq", 100),
        ("worker-techid", WorkerKeys.TechnologyIdentification, "rabbitmq", 100),
    ];

    private readonly Dictionary<string, DateTimeOffset> _lastScaleUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastScaleDown = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Action<ILogger, Exception?> LogAutoscalerStarted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(1, nameof(StartAsync)),
        "Worker autoscaler background service started.");

    private static readonly Action<ILogger, Exception?> LogAutoscalerStopped = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(2, nameof(StopAsync)),
        "Worker autoscaler background service stopped.");

    private static readonly Action<ILogger, string, int, int, long, Exception?> LogScaleDecision =
        LoggerMessage.Define<string, int, int, long>(
            LogLevel.Information,
            new EventId(3, "ScaleDecision"),
            "Scaling {ServiceName} from {CurrentCount} to {DesiredCount} workers (backlog={Backlog}).");

    private static readonly Action<ILogger, string, string, Exception?> LogScaleSkip =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(4, "ScaleSkip"),
            "Skipping scale for {ServiceName}: {Reason}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogAutoscalerStarted(logger, null);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAndScaleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Autoscaler tick failed. Will retry on next interval.");
                }

                await Task.Delay(ScaleCheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            LogAutoscalerStopped(logger, null);
        }
    }

    private async Task EvaluateAndScaleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

        var settings = await db.WorkerScalingSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.ScaleKey, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var overrides = await db.WorkerScaleTargets.AsNoTracking()
            .ToDictionaryAsync(t => t.ScaleKey, t => t.DesiredCount, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var httpBacklog = await GetHttpQueueBacklogAsync(db, ct).ConfigureAwait(false);
        var rabbitQueues = await GetRabbitQueueDepthsAsync(db, ct).ConfigureAwait(false);
        var currentCounts = await GetCurrentWorkerCountsAsync(ct).ConfigureAwait(false);

        foreach (var worker in WorkerDefinitions)
        {
            var scaleKey = ToScaleKey(worker.ServiceName);
            if (overrides.ContainsKey(scaleKey))
            {
                continue;
            }

            var minTasks = settings.TryGetValue(scaleKey, out var setting) ? setting.MinTasks : 1;
            var maxTasks = settings.TryGetValue(scaleKey, out setting) ? setting.MaxTasks : 50;
            var targetBacklog = settings.TryGetValue(scaleKey, out setting)
                ? setting.TargetBacklogPerTask
                : worker.DefaultTargetBacklog;

            minTasks = Math.Max(0, minTasks);
            maxTasks = Math.Max(minTasks, maxTasks);
            targetBacklog = targetBacklog > 0 ? targetBacklog : Math.Max(1, worker.DefaultTargetBacklog);

            if (!currentCounts.TryGetValue(worker.ServiceName, out var currentCount))
            {
                currentCount = 0;
            }

            long backlog = worker.QueueSource switch
            {
                "http-queue" => httpBacklog,
                "rabbitmq" => rabbitQueues.TryGetValue(worker.WorkerKey, out var rmqDepth) ? rmqDepth : 0,
                _ => 0,
            };

            var desiredCount = CalculateDesiredCount(backlog, targetBacklog, minTasks, maxTasks);

            await ApplyScaleIfNeededAsync(
                    worker.ServiceName,
                    currentCount,
                    desiredCount,
                    backlog,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static string ToScaleKey(string serviceName) =>
        serviceName.Replace("worker-", "worker-", StringComparison.Ordinal);

    private static async Task<long> GetHttpQueueBacklogAsync(ArgusDbContext db, CancellationToken ct)
    {
        try
        {
            var pendingStates = new[] { HttpRequestQueueState.Queued, HttpRequestQueueState.Retry };
            return await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => pendingStates.Contains(q.State), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<Dictionary<string, long>> GetRabbitQueueDepthsAsync(ArgusDbContext db, CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Placeholder for RabbitMQ management API integration.
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch
        {
            // Fallback: return empty dictionary.
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, int>> GetCurrentWorkerCountsAsync(CancellationToken ct)
    {
        try
        {
            return await DockerComposeWorkerScaler
                .GetRunningServiceCountsAsync(configuration, logger, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query Docker Compose for worker counts");
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int CalculateDesiredCount(long backlog, int targetBacklogPerTask, int minTasks, int maxTasks)
    {
        if (backlog <= 0)
        {
            return minTasks;
        }

        var safeTargetBacklog = Math.Max(1, targetBacklogPerTask);
        var required = (int)Math.Ceiling((double)backlog / safeTargetBacklog);
        return Math.Clamp(required, minTasks, maxTasks);
    }

    private async Task ApplyScaleIfNeededAsync(
        string serviceName,
        int currentCount,
        int desiredCount,
        long backlog,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (desiredCount > currentCount)
        {
            if (_lastScaleUp.TryGetValue(serviceName, out var lastUp) && now - lastUp < ScaleUpCooldown)
            {
                LogScaleSkip(logger, serviceName, "scale-up cooldown active", null);
                return;
            }
        }
        else if (desiredCount < currentCount)
        {
            if (_lastScaleDown.TryGetValue(serviceName, out var lastDown) && now - lastDown < ScaleDownCooldown)
            {
                LogScaleSkip(logger, serviceName, "scale-down cooldown active", null);
                return;
            }
        }

        if (desiredCount == currentCount)
        {
            return;
        }

        LogScaleDecision(logger, serviceName, currentCount, desiredCount, backlog, null);

        var success = await ScaleDockerServiceAsync(serviceName, desiredCount, ct).ConfigureAwait(false);
        if (!success)
        {
            return;
        }

        if (desiredCount > currentCount)
        {
            _lastScaleUp[serviceName] = now;
        }
        else
        {
            _lastScaleDown[serviceName] = now;
        }
    }

    private async Task<bool> ScaleDockerServiceAsync(string serviceName, int desiredCount, CancellationToken ct)
    {
        try
        {
            await DockerComposeWorkerScaler
                .ScaleWorkerAsync(serviceName, desiredCount, configuration, logger, ct)
                .ConfigureAwait(false);

            logger.LogInformation("Scaled {ServiceName} to {DesiredCount} containers", serviceName, desiredCount);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to scale {ServiceName}", serviceName);
            return false;
        }
    }
}
