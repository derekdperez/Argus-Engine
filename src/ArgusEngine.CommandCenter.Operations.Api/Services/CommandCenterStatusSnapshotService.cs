using System.Reflection;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Net.Sockets;
using ArgusEngine.Infrastructure.Messaging;


namespace ArgusEngine.CommandCenter.Operations.Api.Services;

public sealed class CommandCenterStatusSnapshotService(
    ArgusDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    WorkerScaleDefinitionProvider workerDefinitions,
    IConnectionMultiplexer redis,
    IOptions<RabbitMqOptions> rabbitOptions,
    ILogger<CommandCenterStatusSnapshotService> logger) : ICommandCenterStatusSnapshotService
{
    private const double HttpQueueAgeWarningSeconds = 300;
    private const double HttpQueueAgeCriticalSeconds = 1_800;
    private const long HttpQueueDepthWarning = 1_000;
    private const long HttpQueueDepthCritical = 10_000;
    private const long OutboxDepthWarning = 100;
    private const long OutboxDepthCritical = 1_000;

    public async Task<CommandCenterStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var version = ReadVersion();
        var buildStamp = ReadBuildStamp(version);
        var alerts = new List<CommandCenterAlert>();

        var databaseHealthy = await CanConnectToDatabaseAsync(alerts, now, cancellationToken).ConfigureAwait(false);
        var components = BuildComponents(version, databaseHealthy);
        var dependencies = await BuildDependenciesAsync(databaseHealthy, cancellationToken).ConfigureAwait(false);
        var workers = databaseHealthy
            ? await BuildWorkerStatusesAsync(alerts, now, cancellationToken).ConfigureAwait(false)
            : BuildUnavailableWorkerStatuses();
        var queues = databaseHealthy
            ? await BuildQueueStatusesAsync(alerts, now, cancellationToken).ConfigureAwait(false)
            : BuildUnavailableQueueStatuses();

        var indicators = BuildIndicators(workers, queues, dependencies);
        AddIndicatorAlerts(indicators, alerts, now);

        var aggregate = AggregateStatus(
            components.Select(x => x.Color)
                .Concat(dependencies.Select(x => x.Color))
                .Concat(workers.Select(x => x.Color))
                .Concat(queues.Select(x => x.Color))
                .Concat(indicators.Select(x => x.Color)));

        RecordOperationalMetrics(workers, queues, dependencies, alerts);

        return new CommandCenterStatusSnapshot(
            AtUtc: now,
            Status: aggregate.Status,
            Color: aggregate.Color,
            Version: version,
            BuildStamp: buildStamp,
            Components: components,
            Workers: workers,
            Queues: queues,
            Dependencies: dependencies,
            Indicators: indicators,
            Alerts: alerts);
    }

    private async Task<bool> CanConnectToDatabaseAsync(
        ICollection<CommandCenterAlert> alerts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to connect to the Argus database while building the Command Center status snapshot.");

            alerts.Add(new CommandCenterAlert(
                Severity: "Critical",
                Scope: "postgres",
                Message: "Postgres is unavailable or rejected the health check.",
                AtUtc: now,
                Color: "red"));

            return false;
        }
    }

    private IReadOnlyList<CommandCenterComponentStatus> BuildComponents(string version, bool databaseHealthy)
    {
        var commandCenter = new CommandCenterComponentStatus(
            Key: "command-center",
            DisplayName: "Command Center",
            Version: version,
            Status: "Healthy",
            Color: "green",
            Reason: $"Running in {environment.EnvironmentName}.");

        var persistence = new CommandCenterComponentStatus(
            Key: "persistence",
            DisplayName: "Persistence Layer",
            Version: version,
            Status: databaseHealthy ? "Healthy" : "Critical",
            Color: databaseHealthy ? "green" : "red",
            Reason: databaseHealthy ? "ArgusDbContext can connect." : "ArgusDbContext cannot connect.");

        var infrastructure = new CommandCenterComponentStatus(
            Key: "infrastructure",
            DisplayName: "Infrastructure",
            Version: version,
            Status: "Healthy",
            Color: "green",
            Reason: "Configuration, messaging, and observability services are registered.");

        return [commandCenter, persistence, infrastructure];
    }

    private async Task<IReadOnlyList<CommandCenterWorkerStatus>> BuildWorkerStatusesAsync(
        ICollection<CommandCenterAlert> alerts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var heartbeats = await db.WorkerHeartbeats
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var heartbeatLookup = heartbeats
            .GroupBy(x => x.WorkerKey)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var switches = await db.WorkerSwitches
            .AsNoTracking()
            .ToDictionaryAsync(x => x.WorkerKey, x => x.IsEnabled, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);

        var scaleTargets = await db.WorkerScaleTargets
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ScaleKey, x => x.DesiredCount, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<CommandCenterWorkerStatus>();

        foreach (var definition in workerDefinitions.WorkerScaleDefinitions)
        {
            var desiredCount = ResolveDesiredCount(definition.ScaleKey, scaleTargets);
            
            // Check heartbeats for this worker key (some scale keys match worker keys)
            heartbeatLookup.TryGetValue(definition.ScaleKey, out var workerHeartbeats);
            var runningCount = workerHeartbeats?.Count(h => (now - h.LastHeartbeatUtc).TotalMinutes < 2) ?? 0;
            var pendingCount = Math.Max(0, desiredCount - runningCount);

            string status;
            string color;
            string reason;

            bool isHealthy = workerHeartbeats?.All(h => h.IsHealthy) ?? true;
            var failedHealth = workerHeartbeats?.FirstOrDefault(h => !h.IsHealthy);

            if (desiredCount <= 0)
            {
                status = "Idle";
                color = "gray";
                reason = "No desired workers are configured for this scale group.";
            }
            else if (runningCount == 0)
            {
                status = "Offline";
                color = "red";
                reason = "No active heartbeats detected in the last 2 minutes.";
                
                alerts.Add(new CommandCenterAlert(
                    Severity: "Critical",
                    Scope: definition.DisplayName,
                    Message: "No running instances detected for this worker group.",
                    AtUtc: now,
                    Color: "red"));
            }
            else if (!isHealthy)
            {
                status = "Unhealthy";
                color = "red";
                reason = failedHealth?.HealthMessage ?? "One or more instances reported a health check failure.";

                alerts.Add(new CommandCenterAlert(
                    Severity: "Critical",
                    Scope: definition.DisplayName,
                    Message: reason,
                    AtUtc: now,
                    Color: "red"));
            }
            else if (runningCount < desiredCount)
            {
                status = "Scaling";
                color = "yellow";
                reason = $"Running count ({runningCount}) is below desired ({desiredCount}).";
            }
            else
            {
                status = "Healthy";
                color = "green";
                reason = "Scale group has desired capacity and all instances are healthy.";
            }

            result.Add(new CommandCenterWorkerStatus(
                Key: definition.ScaleKey,
                DisplayName: definition.DisplayName,
                DesiredCount: desiredCount,
                RunningCount: runningCount,
                PendingCount: pendingCount,
                Status: status,
                Color: color,
                Reason: reason));
        }

        foreach (var requiredWorkerKey in workerDefinitions.RequiredWorkerKeys)
        {
            if (result.Any(r => string.Equals(r.Key, requiredWorkerKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!switches.TryGetValue(requiredWorkerKey, out var enabled) || enabled)
            {
                // If it's enabled but not in the scale definitions, check if it's running as a singleton
                heartbeatLookup.TryGetValue(requiredWorkerKey, out var workerHeartbeats);
                var isRunning = workerHeartbeats?.Any(h => (now - h.LastHeartbeatUtc).TotalMinutes < 2) ?? false;
                var isHealthy = workerHeartbeats?.All(h => h.IsHealthy) ?? true;
                
                if (!isRunning)
                {
                    result.Add(new CommandCenterWorkerStatus(
                        Key: requiredWorkerKey,
                        DisplayName: requiredWorkerKey,
                        DesiredCount: 1,
                        RunningCount: 0,
                        PendingCount: 1,
                        Status: "Offline",
                        Color: "red",
                        Reason: "No active heartbeat detected."));
                }
                else if (!isHealthy)
                {
                    result.Add(new CommandCenterWorkerStatus(
                        Key: requiredWorkerKey,
                        DisplayName: requiredWorkerKey,
                        DesiredCount: 1,
                        RunningCount: 1,
                        PendingCount: 0,
                        Status: "Unhealthy",
                        Color: "red",
                        Reason: workerHeartbeats?.FirstOrDefault(h => !h.IsHealthy)?.HealthMessage ?? "Health check failure."));
                }
                
                continue;
            }

            alerts.Add(new CommandCenterAlert(
                Severity: "Critical",
                Scope: requiredWorkerKey,
                Message: $"{requiredWorkerKey} is disabled by worker switch.",
                AtUtc: now,
                Color: "red"));

            result.Add(new CommandCenterWorkerStatus(
                Key: requiredWorkerKey,
                DisplayName: requiredWorkerKey,
                DesiredCount: 1,
                RunningCount: 0,
                PendingCount: 1,
                Status: "Disabled",
                Color: "red",
                Reason: "Worker switch is disabled."));
        }

        return result;
    }

    private List<CommandCenterWorkerStatus> BuildUnavailableWorkerStatuses()
    {
        return workerDefinitions.WorkerScaleDefinitions
            .Select(definition => new CommandCenterWorkerStatus(
                Key: definition.ScaleKey,
                DisplayName: definition.DisplayName,
                DesiredCount: 0,
                RunningCount: 0,
                PendingCount: 0,
                Status: "Unknown",
                Color: "gray",
                Reason: "Database is unavailable; worker switches and scaling settings cannot be read."))
            .ToList();
    }

    private int ResolveDesiredCount(
        string scaleKey,
        IReadOnlyDictionary<string, int> scaleTargets)
    {
        if (scaleTargets.TryGetValue(scaleKey, out var desired))
        {
            return Math.Max(0, desired);
        }

        var defaults = workerDefinitions.DefaultWorkerScalingSetting(scaleKey);
        return Math.Max(0, defaults.MinTasks);
    }

    private async Task<List<CommandCenterQueueStatus>> BuildQueueStatusesAsync(
        List<CommandCenterAlert> alerts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pendingStates = new[] { HttpRequestQueueState.Queued, HttpRequestQueueState.Retry };
        var httpQueue = db.HttpRequestQueue.AsNoTracking().Where(x => pendingStates.Contains(x.State));
        var httpDepth = await httpQueue.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var oldestHttpItem = await httpQueue
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => (DateTimeOffset?)x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var httpAgeSeconds = oldestHttpItem is null ? (double?)null : Math.Max(0, (now - oldestHttpItem.Value).TotalSeconds);
        var failedLast24Hours = await db.HttpRequestQueue
            .AsNoTracking()
            .LongCountAsync(
                x => x.State == HttpRequestQueueState.Failed && x.UpdatedAtUtc >= now.AddHours(-24),
                cancellationToken)
            .ConfigureAwait(false);

        if (failedLast24Hours > 0)
        {
            alerts.Add(new CommandCenterAlert(
                Severity: "Warning",
                Scope: "http-request-queue",
                Message: $"{failedLast24Hours:N0} HTTP queue item(s) failed in the last 24 hours.",
                AtUtc: now,
                Color: "yellow"));
        }

        var outboxStats = await db.OutboxMessages
            .AsNoTracking()
            .GroupBy(x => x.State)
            .Select(g => new { State = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.State, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var outboxDepth = outboxStats.Values.Sum();
        var pendingOutbox = outboxStats.GetValueOrDefault(OutboxMessageState.Pending, 0)
                          + outboxStats.GetValueOrDefault(OutboxMessageState.Failed, 0)
                          + outboxStats.GetValueOrDefault(OutboxMessageState.InFlight, 0);
        var deadLetterOutbox = outboxStats.GetValueOrDefault(OutboxMessageState.DeadLetter, 0);

        var busJournalLast5Minutes = await db.BusJournal
            .AsNoTracking()
            .LongCountAsync(x => x.OccurredAtUtc >= now.AddMinutes(-5), cancellationToken)
            .ConfigureAwait(false);

        var outboxReason = outboxDepth == 0
            ? "No messages in outbox."
            : $"Total: {outboxDepth:N0} (Pending/Failed: {pendingOutbox:N0}, DeadLetter: {deadLetterOutbox:N0}, Succeeded: {outboxStats.GetValueOrDefault(OutboxMessageState.Succeeded, 0):N0})";

        return
        [
            BuildQueueStatus(
                key: "http-request-queue",
                displayName: "HTTP Request Queue",
                depth: httpDepth,
                oldestAgeSeconds: httpAgeSeconds,
                warningDepth: HttpQueueDepthWarning,
                criticalDepth: HttpQueueDepthCritical,
                warningAgeSeconds: HttpQueueAgeWarningSeconds,
                criticalAgeSeconds: HttpQueueAgeCriticalSeconds),
            new CommandCenterQueueStatus(
                Key: "outbox",
                DisplayName: "Outbox",
                Depth: outboxDepth,
                OldestAgeSeconds: null,
                Status: deadLetterOutbox > 0 ? "Degraded" : (pendingOutbox > OutboxDepthWarning ? "Warning" : "Healthy"),
                Color: deadLetterOutbox > 0 ? "red" : (pendingOutbox > OutboxDepthWarning ? "yellow" : "green"),
                Reason: outboxReason),
            new CommandCenterQueueStatus(
                Key: "bus-journal",
                DisplayName: "Bus Journal",
                Depth: busJournalLast5Minutes,
                OldestAgeSeconds: null,
                Status: "Healthy",
                Color: "green",
                Reason: $"{busJournalLast5Minutes:N0} bus journal entries recorded in the last 5 minutes.")
        ];
    }

    private static IReadOnlyList<CommandCenterQueueStatus> BuildUnavailableQueueStatuses()
    {
        return
        [
            new CommandCenterQueueStatus(
                Key: "http-request-queue",
                DisplayName: "HTTP Request Queue",
                Depth: null,
                OldestAgeSeconds: null,
                Status: "Unknown",
                Color: "gray",
                Reason: "Database is unavailable; queue depth cannot be read."),
            new CommandCenterQueueStatus(
                Key: "outbox",
                DisplayName: "Outbox",
                Depth: null,
                OldestAgeSeconds: null,
                Status: "Unknown",
                Color: "gray",
                Reason: "Database is unavailable; outbox depth cannot be read."),
            new CommandCenterQueueStatus(
                Key: "bus-journal",
                DisplayName: "Bus Journal",
                Depth: null,
                OldestAgeSeconds: null,
                Status: "Unknown",
                Color: "gray",
                Reason: "Database is unavailable; bus activity cannot be read.")
        ];
    }

    private static CommandCenterQueueStatus BuildQueueStatus(
        string key,
        string displayName,
        long depth,
        double? oldestAgeSeconds,
        long warningDepth,
        long criticalDepth,
        double? warningAgeSeconds,
        double? criticalAgeSeconds)
    {
        var critical = depth >= criticalDepth || (criticalAgeSeconds.HasValue && oldestAgeSeconds >= criticalAgeSeconds.Value);
        var warning = depth >= warningDepth || (warningAgeSeconds.HasValue && oldestAgeSeconds >= warningAgeSeconds.Value);

        if (critical)
        {
            return new CommandCenterQueueStatus(
                Key: key,
                DisplayName: displayName,
                Depth: depth,
                OldestAgeSeconds: oldestAgeSeconds,
                Status: "Critical",
                Color: "red",
                Reason: "Queue exceeded a critical depth or age threshold.");
        }

        if (warning)
        {
            return new CommandCenterQueueStatus(
                Key: key,
                DisplayName: displayName,
                Depth: depth,
                OldestAgeSeconds: oldestAgeSeconds,
                Status: "Degraded",
                Color: "yellow",
                Reason: "Queue exceeded a warning depth or age threshold.");
        }

        return new CommandCenterQueueStatus(
            Key: key,
            DisplayName: displayName,
            Depth: depth,
            OldestAgeSeconds: oldestAgeSeconds,
            Status: "Healthy",
            Color: "green",
            Reason: depth == 0 ? "No pending work." : "Backlog is within thresholds.");
    }

    private async Task<IReadOnlyList<CommandCenterDependencyStatus>> BuildDependenciesAsync(
        bool databaseHealthy,
        CancellationToken ct)
    {
        var rabbit = rabbitOptions.Value;
        var rabbitConnected = await CheckRabbitMqConnectivityAsync(rabbit.Host, rabbit.Port, ct).ConfigureAwait(false);
        var redisConnected = await CheckRedisConnectivityAsync(ct).ConfigureAwait(false);

        return
        [
            new CommandCenterDependencyStatus(
                Key: "postgres",
                DisplayName: "Postgres",
                Status: databaseHealthy ? "Healthy" : "Critical",
                Color: databaseHealthy ? "green" : "red",
                Reason: databaseHealthy ? "Database connection check succeeded." : "Database connection check failed."),
            new CommandCenterDependencyStatus(
                Key: "rabbitmq",
                DisplayName: "RabbitMQ",
                Status: rabbitConnected ? "Healthy" : "Critical",
                Color: rabbitConnected ? "green" : "red",
                Reason: rabbitConnected ? $"Connected to {rabbit.Host}:{rabbit.Port}." : $"Failed to connect to {rabbit.Host}:{rabbit.Port}."),
            new CommandCenterDependencyStatus(
                Key: "redis",
                DisplayName: "Redis",
                Status: redisConnected ? "Healthy" : "Critical",
                Color: redisConnected ? "green" : "red",
                Reason: redisConnected ? "Redis connection check succeeded." : "Redis connection check failed."),
            new CommandCenterDependencyStatus(
                Key: "file-store",
                DisplayName: "File Store",
                Status: databaseHealthy ? "Healthy" : "Unknown",
                Color: databaseHealthy ? "green" : "gray",
                Reason: databaseHealthy ? "File store database is reachable via Postgres connection." : "File store health check skipped due to Postgres failure.")
        ];
    }

    private async Task<bool> CheckRabbitMqConnectivityAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port, ct).AsTask();
            var timeoutTask = Task.Delay(2000, ct);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            return completedTask == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckRedisConnectivityAsync(CancellationToken ct)
    {
        try
        {
            if (!redis.IsConnected)
            {
                return false;
            }

            var database = redis.GetDatabase();
            await database.PingAsync().WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<CommandCenterSloIndicator> BuildIndicators(
        IReadOnlyList<CommandCenterWorkerStatus> workers,
        IReadOnlyList<CommandCenterQueueStatus> queues,
        IReadOnlyList<CommandCenterDependencyStatus> dependencies)
    {
        var httpQueue = queues.FirstOrDefault(x => x.Key == "http-request-queue");
        var outbox = queues.FirstOrDefault(x => x.Key == "outbox");
        var postgres = dependencies.FirstOrDefault(x => x.Key == "postgres");
        var disabledWorkers = workers.Count(x => x.Color == "red");

        return
        [
            new CommandCenterSloIndicator(
                Name: "HTTP queue age",
                Target: "< 5 minutes",
                CurrentValue: httpQueue?.OldestAgeSeconds is null
                    ? "No pending work"
                    : $"{Math.Round(httpQueue.OldestAgeSeconds.Value / 60, 1)} minutes",
                Status: httpQueue?.Status ?? "Unknown",
                Color: httpQueue?.Color ?? "gray",
                Reason: httpQueue?.Reason ?? "HTTP queue metrics are unavailable."),
            new CommandCenterSloIndicator(
                Name: "HTTP queue depth",
                Target: $"< {HttpQueueDepthWarning:N0}",
                CurrentValue: httpQueue?.Depth?.ToString("N0") ?? "Unknown",
                Status: httpQueue?.Status ?? "Unknown",
                Color: httpQueue?.Color ?? "gray",
                Reason: httpQueue?.Reason ?? "HTTP queue metrics are unavailable."),
            new CommandCenterSloIndicator(
                Name: "Outbox backlog",
                Target: $"< {OutboxDepthWarning:N0}",
                CurrentValue: outbox?.Depth?.ToString("N0") ?? "Unknown",
                Status: outbox?.Status ?? "Unknown",
                Color: outbox?.Color ?? "gray",
                Reason: outbox?.Reason ?? "Outbox metrics are unavailable."),
            new CommandCenterSloIndicator(
                Name: "Worker availability",
                Target: "Required workers enabled",
                CurrentValue: disabledWorkers == 0 ? "All enabled" : $"{disabledWorkers} disabled",
                Status: disabledWorkers == 0 ? "Healthy" : "Critical",
                Color: disabledWorkers == 0 ? "green" : "red",
                Reason: disabledWorkers == 0
                    ? "No required worker is disabled."
                    : "One or more required workers are disabled."),
            new CommandCenterSloIndicator(
                Name: "Postgres connectivity",
                Target: "Connected",
                CurrentValue: postgres?.Status ?? "Unknown",
                Status: postgres?.Status ?? "Unknown",
                Color: postgres?.Color ?? "gray",
                Reason: postgres?.Reason ?? "Postgres health is unavailable.")
        ];
    }

    private static void AddIndicatorAlerts(
        IReadOnlyList<CommandCenterSloIndicator> indicators,
        ICollection<CommandCenterAlert> alerts,
        DateTimeOffset now)
    {
        foreach (var indicator in indicators.Where(x => x.Color is "yellow" or "red"))
        {
            alerts.Add(new CommandCenterAlert(
                Severity: indicator.Color == "red" ? "Critical" : "Warning",
                Scope: indicator.Name,
                Message: indicator.Reason,
                AtUtc: now,
                Color: indicator.Color));
        }
    }

    private static (string Status, string Color) AggregateStatus(IEnumerable<string> colors)
    {
        var colorList = colors.ToList();

        if (colorList.Any(x => string.Equals(x, "red", StringComparison.OrdinalIgnoreCase)))
        {
            return ("Critical", "red");
        }

        if (colorList.Any(x => string.Equals(x, "yellow", StringComparison.OrdinalIgnoreCase)))
        {
            return ("Degraded", "yellow");
        }

        return ("Healthy", "green");
    }

    private static void RecordOperationalMetrics(
        IReadOnlyList<CommandCenterWorkerStatus> workers,
        IReadOnlyList<CommandCenterQueueStatus> queues,
        IReadOnlyList<CommandCenterDependencyStatus> dependencies,
        IReadOnlyList<CommandCenterAlert> alerts)
    {
        foreach (var queue in queues)
        {
            if (queue.Depth.HasValue)
            {
                if (queue.Key == "http-request-queue")
                {
                    ArgusMeters.HttpQueueDepth.Add(queue.Depth.Value, new KeyValuePair<string, object?>("queue", queue.Key));
                }
                else if (queue.Key == "outbox")
                {
                    ArgusMeters.OutboxDepth.Add(queue.Depth.Value, new KeyValuePair<string, object?>("queue", queue.Key));
                }
            }

            if (queue.OldestAgeSeconds.HasValue)
            {
                ArgusMeters.HttpQueueOldestAgeSeconds.Record(
                    queue.OldestAgeSeconds.Value,
                    new KeyValuePair<string, object?>("queue", queue.Key));
            }
        }

        foreach (var worker in workers)
        {
            ArgusMeters.WorkerDesiredCount.Add(
                worker.DesiredCount,
                new KeyValuePair<string, object?>("worker", worker.Key));

            ArgusMeters.WorkerRunningCount.Add(
                worker.RunningCount,
                new KeyValuePair<string, object?>("worker", worker.Key));
        }

        foreach (var dependency in dependencies)
        {
            var value = dependency.Color switch
            {
                "green" => 1,
                "yellow" => 0,
                "red" => -1,
                _ => 0
            };

            ArgusMeters.DependencyHealth.Add(
                value,
                new KeyValuePair<string, object?>("dependency", dependency.Key));
        }

        foreach (var alertGroup in alerts.GroupBy(x => x.Severity))
        {
            ArgusMeters.OperationalAlerts.Add(
                alertGroup.LongCount(),
                new KeyValuePair<string, object?>("severity", alertGroup.Key));
        }
    }

    private string ReadBuildStamp()
    {
        return ReadBuildStamp(ReadVersion());
    }

    private string ReadBuildStamp(string fallback)
    {
        return configuration.GetArgusValue("Release:BuildStamp")
               ?? configuration["ARGUS_BUILD_STAMP"]
               ?? configuration["BUILD_VERSION"]
               ?? fallback;
    }

    private static string ReadVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    }
}




