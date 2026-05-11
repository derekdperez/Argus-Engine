using System.Diagnostics;
using System.Text.Json;
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
    private static readonly TimeSpan ScaleCheckInterval = TimeSpan.FromSeconds(
        Random.Shared.Next(28, 32)); // Add jitter to avoid thundering herd.

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

    private static readonly Action<ILogger, Exception?> LogAutoscalerStarted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(StartAsync)),
            "Worker autoscaler background service started.");

    private static readonly Action<ILogger, Exception?> LogAutoscalerStopped =
        LoggerMessage.Define(
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
                ct).ConfigureAwait(false);
        }
    }

    private static string ToScaleKey(string serviceName) => serviceName.Replace("worker-", "worker-", StringComparison.Ordinal);

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
            // Fallback: return empty dict.
        }

        return result;
    }

    private async Task<Dictionary<string, int>> GetCurrentWorkerCountsAsync(CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var projectName = configuration["Argus:Autoscaler:DockerComposeProject"] ?? "argus-engine";

        try
        {
            var result = await RunCommandAsync(
                "docker",
                $"ps --filter label=com.docker.compose.project={projectName} --format \"{{{{json .}}}}\"",
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                logger.LogWarning(
                    "Docker worker count query failed: {Error}",
                    string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

                return counts;
            }

            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var labels = root.TryGetProperty("Labels", out var labelsProp)
                        ? labelsProp.GetString() ?? string.Empty
                        : string.Empty;

                    var serviceName = ExtractLabel(labels, "com.docker.compose.service");

                    if (string.IsNullOrWhiteSpace(serviceName))
                    {
                        continue;
                    }

                    counts.TryGetValue(serviceName, out var current);
                    counts[serviceName] = current + 1;
                }
                catch (JsonException ex)
                {
                    logger.LogDebug(ex, "Skipping malformed docker ps JSON line: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query Docker for worker counts");
        }

        return counts;
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
            if (_lastScaleUp.TryGetValue(serviceName, out var lastUp) &&
                now - lastUp < ScaleUpCooldown)
            {
                LogScaleSkip(logger, serviceName, "scale-up cooldown active", null);
                return;
            }
        }
        else if (desiredCount < currentCount)
        {
            if (_lastScaleDown.TryGetValue(serviceName, out var lastDown) &&
                now - lastDown < ScaleDownCooldown)
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
            var composePath = configuration["Argus:Autoscaler:DockerComposePath"]
                ?? "/home/derekdperez_dev/argus-engine/deploy/docker-compose.yml";

            var projectName = configuration["Argus:Autoscaler:DockerComposeProject"] ?? "argus-engine";

            var args =
                $"compose -p {projectName} -f {composePath} up -d --no-build --no-deps --scale {serviceName}={desiredCount} {serviceName}";

            var result = await RunCommandAsync("docker", args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

            if (!result.Success)
            {
                logger.LogWarning(
                    "Docker scale command failed for {ServiceName}: {Error}",
                    serviceName,
                    string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

                return false;
            }

            logger.LogInformation("Scaled {ServiceName} to {DesiredCount} containers", serviceName, desiredCount);
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
        if (string.IsNullOrWhiteSpace(labelsString))
        {
            return string.Empty;
        }

        foreach (var part in labelsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);

            if (eq <= 0)
            {
                continue;
            }

            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();

            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return string.Empty;
    }

    private static async Task<(bool Success, string Output, string Error)> RunCommandAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            if (!process.Start())
            {
                return (false, string.Empty, "process.Start() returned false");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? (true, stdout, stderr)
                : (false, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (false, string.Empty, "command timed out or was cancelled");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return (false, string.Empty, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
