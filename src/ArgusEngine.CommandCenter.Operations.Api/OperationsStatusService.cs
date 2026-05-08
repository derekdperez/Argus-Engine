using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace ArgusEngine.CommandCenter.Operations.Api;

internal sealed class OperationsStatusService(
    ArgusDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IConnectionMultiplexer redis,
    ILogger<OperationsStatusService> logger)
{
    private static readonly Action<ILogger, Exception?> LogDatabaseStatusFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(CheckDatabaseAsync)),
            "Unable to connect to the Argus database while building the Operations API status snapshot.");

    public async Task<CommandCenterStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var version = ReadVersion();
        var alerts = new List<CommandCenterAlert>();
        var databaseHealthy = await CheckDatabaseAsync(alerts, now, cancellationToken).ConfigureAwait(false);
        var redisHealthy = await CheckRedisAsync(cancellationToken).ConfigureAwait(false);
        var rabbitHealthy = await CheckRabbitMqAsync(cancellationToken).ConfigureAwait(false);

        var queues = databaseHealthy
            ? await BuildQueuesAsync(now, alerts, cancellationToken).ConfigureAwait(false)
            : BuildUnavailableQueues();
        var workers = databaseHealthy
            ? await BuildWorkersAsync(cancellationToken).ConfigureAwait(false)
            : [];

        var components = new[]
        {
            new CommandCenterComponentStatus("operations-api", "Operations API", version, "Healthy", "green", $"Running in {environment.EnvironmentName}."),
            new CommandCenterComponentStatus("persistence", "Persistence Layer", version, databaseHealthy ? "Healthy" : "Critical", databaseHealthy ? "green" : "red", databaseHealthy ? "ArgusDbContext can connect." : "ArgusDbContext cannot connect."),
        };
        var dependencies = new[]
        {
            new CommandCenterDependencyStatus("postgres", "PostgreSQL", databaseHealthy ? "Healthy" : "Critical", databaseHealthy ? "green" : "red", databaseHealthy ? "Database connectivity succeeded." : "Database connectivity failed."),
            new CommandCenterDependencyStatus("redis", "Redis", redisHealthy ? "Healthy" : "Critical", redisHealthy ? "green" : "red", redisHealthy ? "Redis ping succeeded." : "Redis ping failed."),
            new CommandCenterDependencyStatus("rabbitmq", "RabbitMQ", rabbitHealthy ? "Healthy" : "Critical", rabbitHealthy ? "green" : "red", rabbitHealthy ? "RabbitMQ TCP port accepted connections." : "RabbitMQ TCP check failed."),
        };
        var indicators = BuildIndicators(queues, dependencies);
        var color = SelectWorstColor(
            components.Select(c => c.Color)
                .Concat(dependencies.Select(d => d.Color))
                .Concat(queues.Select(q => q.Color))
                .Concat(workers.Select(w => w.Color))
                .Concat(indicators.Select(i => i.Color)));

        return new CommandCenterStatusSnapshot(
            now,
            ColorToStatus(color),
            color,
            version,
            Environment.GetEnvironmentVariable("ARGUS_BUILD_STAMP")
                ?? Environment.GetEnvironmentVariable("BUILD_SOURCE_STAMP")
                ?? version,
            components,
            workers,
            queues,
            dependencies,
            indicators,
            alerts);
    }

    private async Task<bool> CheckDatabaseAsync(List<CommandCenterAlert> alerts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDatabaseStatusFailed(logger, ex);
            alerts.Add(new CommandCenterAlert("Critical", "postgres", "Postgres is unavailable or rejected the health check.", now, "red"));
            return false;
        }
    }

    private async Task<bool> CheckRedisAsync(CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return redis.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckRabbitMqAsync(CancellationToken cancellationToken)
    {
        var endpoint = ResolveRabbitEndpoint();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<CommandCenterQueueStatus>> BuildQueuesAsync(
        DateTimeOffset now,
        List<CommandCenterAlert> alerts,
        CancellationToken cancellationToken)
    {
        var queued = await db.HttpRequestQueue.AsNoTracking()
            .LongCountAsync(q => q.State == HttpRequestQueueState.Queued, cancellationToken)
            .ConfigureAwait(false);
        var retryReady = await db.HttpRequestQueue.AsNoTracking()
            .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now, cancellationToken)
            .ConfigureAwait(false);
        var outbox = await db.OutboxMessages.AsNoTracking()
            .LongCountAsync(e => e.DispatchedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        var oldestHttp = await db.HttpRequestQueue.AsNoTracking()
            .Where(q => q.State == HttpRequestQueueState.Queued || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now))
            .OrderBy(q => q.CreatedAtUtc)
            .Select(q => (DateTimeOffset?)q.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var httpDepth = queued + retryReady;
        var httpAge = oldestHttp is null ? null : (double?)(now - oldestHttp.Value).TotalSeconds;
        var httpColor = httpDepth > 10_000 || httpAge > 1_800 ? "red" : httpDepth > 1_000 || httpAge > 300 ? "yellow" : "green";
        if (httpColor != "green")
        {
            alerts.Add(new CommandCenterAlert(httpColor == "red" ? "Critical" : "Warning", "http-request-queue", "HTTP request queue backlog is elevated.", now, httpColor));
        }

        var outboxColor = outbox > 1_000 ? "red" : outbox > 100 ? "yellow" : "green";
        return
        [
            new CommandCenterQueueStatus("http-request-queue", "HTTP Request Queue", httpDepth, httpAge, ColorToStatus(httpColor), httpColor, $"{httpDepth} ready items."),
            new CommandCenterQueueStatus("event-outbox", "Event Outbox", outbox, null, ColorToStatus(outboxColor), outboxColor, $"{outbox} unpublished messages."),
        ];
    }

    private async Task<IReadOnlyList<CommandCenterWorkerStatus>> BuildWorkersAsync(CancellationToken cancellationToken)
    {
        var rows = await db.WorkerHeartbeats.AsNoTracking()
            .GroupBy(h => h.WorkerKey)
            .Select(g => new
            {
                WorkerKey = g.Key,
                Running = g.Count(h => h.LastHeartbeatUtc >= DateTimeOffset.UtcNow.AddMinutes(-2)),
                LastHeartbeat = g.Max(h => h.LastHeartbeatUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .OrderBy(row => row.WorkerKey, StringComparer.Ordinal)
            .Select(row =>
            {
                var color = row.Running > 0 ? "green" : "yellow";
                return new CommandCenterWorkerStatus(row.WorkerKey, row.WorkerKey, 0, row.Running, 0, ColorToStatus(color), color, $"Last heartbeat {row.LastHeartbeat:u}.");
            })
            .ToList();
    }

    private static IReadOnlyList<CommandCenterQueueStatus> BuildUnavailableQueues() =>
    [
        new("http-request-queue", "HTTP Request Queue", null, null, "Unknown", "gray", "Database unavailable."),
        new("event-outbox", "Event Outbox", null, null, "Unknown", "gray", "Database unavailable."),
    ];

    private static IReadOnlyList<CommandCenterSloIndicator> BuildIndicators(
        IReadOnlyList<CommandCenterQueueStatus> queues,
        CommandCenterDependencyStatus[] dependencies) =>
    [
        new("Dependencies healthy", "All green", $"{dependencies.Count(d => d.Color == "green")}/{dependencies.Length}", dependencies.All(d => d.Color == "green") ? "Healthy" : "Degraded", dependencies.All(d => d.Color == "green") ? "green" : "yellow", "Postgres, Redis, and RabbitMQ dependency checks."),
        new("Queues healthy", "No elevated backlog", $"{queues.Count(q => q.Color == "green")}/{queues.Count}", queues.All(q => q.Color == "green") ? "Healthy" : "Degraded", queues.All(q => q.Color == "green") ? "green" : "yellow", "HTTP queue and outbox backlog checks."),
    ];

    private (string Host, int Port) ResolveRabbitEndpoint()
    {
        var raw = configuration["RabbitMq:Host"]?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("localhost", 5672);
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return (uri.Host, uri.Port > 0 ? uri.Port : 5672);
        }

        var lastColon = raw.LastIndexOf(':');
        return lastColon > 0 && int.TryParse(raw[(lastColon + 1)..], out var port)
            ? (raw[..lastColon], port)
            : (raw, 5672);
    }

    private static string ReadVersion()
    {
        var assembly = typeof(OperationsStatusService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string SelectWorstColor(IEnumerable<string> colors) =>
        colors.Select(ColorRank).DefaultIfEmpty(("gray", 1)).MaxBy(x => x.Item2).Item1;

    private static (string Color, int Rank) ColorRank(string color) =>
        color switch
        {
            "red" => ("red", 3),
            "yellow" => ("yellow", 2),
            "gray" => ("gray", 1),
            "green" => ("green", 0),
            _ => ("gray", 1),
        };

    private static string ColorToStatus(string color) =>
        color switch
        {
            "green" => "Healthy",
            "yellow" => "Degraded",
            "red" => "Critical",
            _ => "Unknown",
        };
}
