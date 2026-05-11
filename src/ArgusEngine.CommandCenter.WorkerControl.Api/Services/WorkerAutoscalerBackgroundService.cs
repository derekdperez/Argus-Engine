using System.Text.Json;
using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

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
    // Scale check interval - run every 30 seconds by default
    private static readonly TimeSpan ScaleCheckInterval = TimeSpan.FromSeconds(
        new Random().Next(28, 32)); // Add jitter to avoid thundering herd

    private static readonly TimeSpan ScaleUpCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ScaleDownCooldown = TimeSpan.FromSeconds(60);

    // Worker definitions with their queue types
    private static readonly (string ServiceName, string WorkerKey, string QueueSource, int DefaultTargetBacklog)[] WorkerDefinitions =
    [
        ("worker-spider", WorkerKeys.Spider, "http-queue", 100),
        ("worker-http-requester", WorkerKeys.HttpRequester, "http-queue", 100),
        ("worker-enum", WorkerKeys.Enumeration, "rabbitmq", 25),
        ("worker-portscan", WorkerKeys.PortScan, "rabbitmq", 100),
        ("worker-highvalue", WorkerKeys.HighValueRegex, "rabbitmq", 100),
        ("worker-techid", WorkerKeys.TechnologyIdentification, "rabbitmq", 100),
    ];

    // Track last scale times to prevent thrashing
    private readonly Dictionary<string, DateTimeOffset> _lastScaleUp = new();
    private readonly Dictionary<string, DateTimeOffset> _lastScaleDown = new();

    private static readonly Action<ILogger, Exception?> LogAutoscalerStarted =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)),
            "Worker autoscaler background service started.");

    private static readonly Action<ILogger, Exception?> LogAutoscalerStopped =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(StopAsync)),
            "Worker autoscaler background service stopped.");

    private static readonly Action<ILogger, string, int, long, int, Exception?> LogScaleDecision =
        LoggerMessage.Define<string, int, long, int>(
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

        // Wait a bit for services to fully initialize
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

            try
            {
                await Task.Delay(ScaleCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogAutoscalerStopped(logger, null);
    }

    private async Task EvaluateAndScaleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

        // Get scaling settings from database
        var settings = await db.WorkerScalingSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.ScaleKey, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // Get scale overrides (manual scaling takes precedence)
        var overrides = await db.WorkerScaleTargets.AsNoTracking()
            .ToDictionaryAsync(t => t.ScaleKey, t => t.DesiredCount, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // Get HTTP queue metrics
        var now = DateTimeOffset.UtcNow;
        var httpBacklog = await GetHttpQueueBacklogAsync(db, ct).ConfigureAwait(false);
        var rabbitQueues = await GetRabbitQueueDepthsAsync(db, ct).ConfigureAwait(false);

        // Get current container state
        var currentCounts = await GetCurrentWorkerCountsAsync(ct).ConfigureAwait(false);

        foreach (var worker in WorkerDefinitions)
        {
            var scaleKey = ToScaleKey(worker.ServiceName);

            // Check if manual override exists
            if (overrides.TryGetValue(scaleKey, out var overrideCount))
            {
                // Manual override is active - skip autoscaling for this worker
                continue;
            }

            // Get min/max settings
            var minTasks = settings.TryGetValue(scaleKey, out var s) ? s.MinTasks : 1;
            var maxTasks = settings.TryGetValue(scaleKey, out s) ? s.MaxTasks : 50;
            var targetBacklog = settings.TryGetValue(scaleKey, out s) ? s.TargetBacklogPerTask : worker.DefaultTargetBacklog;

            // Get current count
            currentCounts.TryGetValue(worker.ServiceName, out var currentCount);
            if (currentCount == null)
                currentCount = 0;

            // Calculate backlog for this worker
            long backlog = worker.QueueSource switch
            {
                "http-queue" => httpBacklog,
                "rabbitmq" => rabbitQueues.TryGetValue(worker.WorkerKey, out var rmqDepth) ? rmqDepth : 0,
                _ => 0
            };

            // Calculate desired count based on backlog
            var desiredCount = CalculateDesiredCount(backlog, targetBacklog, minTasks, maxTasks);

            // Apply scale decision with cooldown protection
            await ApplyScaleIfNeededAsync(
                worker.ServiceName,
                currentCount,
                desiredCount,
                backlog,
                minTasks,
                maxTasks,
                ct).ConfigureAwait(false);
        }
    }

    private static string ToScaleKey(string serviceName) => serviceName.Replace("worker-", "worker-");

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
            // Try to get RabbitMQ queue metrics from the BusJournal or use estimates
            // For now, return empty - in production this would query RabbitMQ management API
            // The worker-specific backlog calculation will fall back to default behavior
            await Task.CompletedTask;
        }
        catch
        {
            // Fallback: return empty dict
        }

        return result;
    }

    private async Task<Dictionary<string, int>> GetCurrentWorkerCountsAsync(CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Query docker for running containers
            var result = await RunCommandAsync(
                "docker",
                "ps --no-trunc --format \"{{.Label}}\" --filter label=com.docker.compose.project=argus-engine 2>/dev/null | grep -E \"^com.docker.compose.service=\" | cut -d= -f2 | sort | uniq -c",
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                // Alternative: parse docker ps JSON output
                var jsonResult = await RunCommandAsync(
                    "docker",
                    "ps --format \"{{json .}}\" --filter label=com.docker.compose.project=argus-engine",
                    TimeSpan.FromSeconds(10),
                    ct).ConfigureAwait(false);

                if (jsonResult.Success)
                {
                    foreach (var line in jsonResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;

                            // Extract service name from labels
                            var labels = root.TryGetProperty("Labels", out var labelsProp)
                                ? labelsProp.GetString() ?? ""
                                : "";

                            var serviceName = ExtractLabel(labels, "com.docker.compose.service");
                            var state = root.TryGetProperty("State", out var stateProp)
                                ? stateProp.GetString() ?? "unknown"
                                : "unknown";

                            if (!string.IsNullOrWhiteSpace(serviceName) &&
                                state.Equals("running", StringComparison.OrdinalIgnoreCase))
                            {
                                counts.TryGetValue(serviceName, out var current);
                                counts[serviceName] = current + 1;
                            }
                        }
                        catch { /* skip malformed lines */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query docker for worker counts");
        }

        return counts;
    }

    private int CalculateDesiredCount(long backlog, int targetBacklogPerTask, int minTasks, int maxTasks)
    {
        if (backlog <= 0)
            return minTasks;

        // Calculate required workers: ceil(backlog / targetBacklogPerTask)
        var required = (int)Math.Ceiling((double)backlog / targetBacklogPerTask);

        // Clamp to min/max range
        return Math.Clamp(required, minTasks, maxTasks);
    }

    private async Task ApplyScaleIfNeededAsync(
        string serviceName,
        int currentCount,
        int desiredCount,
        long backlog,
        int minTasks,
        int maxTasks,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Check cooldowns
        if (desiredCount > currentCount)
        {
            // Scale up - use shorter cooldown
            if (_lastScaleUp.TryGetValue(serviceName, out var lastUp) &&
                (now - lastUp) < ScaleUpCooldown)
            {
                LogScaleSkip(logger, serviceName, "scale-up cooldown active", null);
                return;
            }
        }
        else if (desiredCount < currentCount)
        {
            // Scale down - use longer cooldown
            if (_lastScaleDown.TryGetValue(serviceName, out var lastDown) &&
                (now - lastDown) < ScaleDownCooldown)
            {
                LogScaleSkip(logger, serviceName, "scale-down cooldown active", null);
                return;
            }
        }

        // No change needed
        if (desiredCount == currentCount)
            return;

        LogScaleDecision(logger, serviceName, currentCount, backlog, desiredCount, null);

        var success = await ScaleDockerServiceAsync(serviceName, desiredCount, ct).ConfigureAwait(false);

        if (success)
        {
            if (desiredCount > currentCount)
                _lastScaleUp[serviceName] = now;
            else
                _lastScaleDown[serviceName] = now;
        }
    }

    private async Task<bool> ScaleDockerServiceAsync(string serviceName, int desiredCount, CancellationToken ct)
    {
        try
        {
            // Get docker-compose file path from configuration
            var composePath = configuration["Argus:Autoscaler:DockerComposePath"]
                ?? "/home/derekdperez_dev/argus-engine/deploy/docker-compose.yml";

            // Scale using docker compose
            var args = $"compose -f {composePath} up -d --no-build --scale {serviceName}={desiredCount} --no-recreate {serviceName}";
            var result = await RunCommandAsync("docker", args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                logger.LogWarning("Docker scale command failed for {ServiceName}: {Error}",
                    serviceName, result.Error);
                return false;
            }

            logger.LogInformation("Scaled {ServiceName} to {DesiredCount} containers",
                serviceName, desiredCount);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to scale {ServiceName}", serviceName);
            return false;
        }
    }

    private static string ExtractLabel(string labelsString, string key)
    {
        if (string.IsNullOrWhiteSpace(labelsString)) return "";
        foreach (var part in labelsString.Split(','))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return "";
    }

    private static async Task<(bool Success, string Output, string Error)> RunCommandAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            if (!process.Start()) return (false, "", "process.Start() returned false");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await outTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? (true, stdout, stderr)
                : (false, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            return (false, "", "command timed out or was cancelled");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}