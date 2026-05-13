using System.Globalization;
using ArgusEngine.Application.Workers;
using System.Text.Json;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.CommandCenter.WorkerControl.Api.Services;
using ArgusEngine.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

/// <summary>
/// Docker Compose worker status and manual scaling endpoints.
/// </summary>
public static class DockerWorkerEndpoints
{
    private static readonly WorkerServiceDefinition[] ScalableServices =
    [
        new("worker-spider", "Spider Worker", [WorkerKeys.Spider]),
        new("worker-http-requester", "HTTP Requester", [WorkerKeys.HttpRequester]),
        new("worker-enum", "Enum Worker", [WorkerKeys.Enumeration]),
        new("worker-portscan", "Port Scan Worker", [WorkerKeys.PortScan]),
        new("worker-highvalue", "High Value Worker", [WorkerKeys.HighValueRegex, WorkerKeys.HighValuePaths]),
        new("worker-techid", "Tech ID Worker", [WorkerKeys.TechnologyIdentification]),
    ];

    public static IEndpointRouteBuilder MapDockerWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workers/docker-status", GetDockerStatusAsync)
            .WithName("DockerWorkerStatus");

        app.MapGet("/api/workers/{serviceName}/details", GetWorkerDetailsAsync)
            .WithName("DockerWorkerDetails");

        app.MapPut("/api/workers/{serviceName}/docker-scale", ScaleDockerWorkerAsync)
            .WithName("DockerScaleWorker");

        return app;
    }

    private static async Task<IResult> GetDockerStatusAsync(
        IConfiguration configuration,
        ArgusDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DockerWorkerEndpoints");
        var at = DateTimeOffset.UtcNow;

        try
        {
            var allContainers = await DockerComposeWorkerScaler.GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            var services = ScalableServices
                .Select(def =>
                {
                    var matching = allContainers
                        .Where(c => string.Equals(c.ServiceName, def.ServiceName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var containerDtos = matching
                        .Select(c =>
                        {
                            var color = ContainerStateColor(c.State);
                            return new DockerWorkerContainerDto(
                                DockerComposeWorkerScaler.ShortId(c.Id),
                                c.Name.TrimStart('/'),
                                c.State,
                                c.Status,
                                color);
                        })
                        .ToList();

                    var running = containerDtos.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
                    var overall = running == 0 && containerDtos.Count == 0
                        ? "stopped"
                        : running == containerDtos.Count
                            ? "running"
                            : running == 0 ? "stopped" : "partial";

                    var color = overall == "running" ? "green" : overall == "partial" ? "yellow" : "red";

                    return new DockerWorkerServiceDto(
                        def.ServiceName,
                        def.DisplayName,
                        running,
                        containerDtos.Count,
                        overall,
                        color,
                        containerDtos);
                })
                .ToList();

            return Results.Ok(new DockerWorkerStatusSnapshotDto(at, true, null, services));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker worker status query failed; falling back to worker heartbeat status.");

            var services = await BuildHeartbeatStatusAsync(db, at, ct).ConfigureAwait(false);

            return Results.Ok(new DockerWorkerStatusSnapshotDto(
                at,
                true,
                null,
                services));
        }
    }

    private static async Task<IReadOnlyList<DockerWorkerServiceDto>> BuildHeartbeatStatusAsync(
        ArgusDbContext db,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var cutoff = at.AddMinutes(-2);

        var heartbeats = await db.WorkerHeartbeats
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ScalableServices
            .Select(def =>
            {
                var matching = heartbeats
                    .Where(h => def.WorkerKeys.Contains(h.WorkerKey, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(h => h.WorkerKey)
                    .ThenBy(h => h.HostName)
                    .ToList();

                var containers = matching
                    .Select(h =>
                    {
                        var fresh = h.LastHeartbeatUtc >= cutoff;
                        var healthy = h.IsHealthy;
                        var active = fresh && healthy;
                        var state = active ? "running" : fresh ? "unhealthy" : "stale";
                        var color = active ? "green" : fresh ? "yellow" : "red";
                        var status = active
                            ? $"heartbeat {FormatAge(at - h.LastHeartbeatUtc)} ago"
                            : !string.IsNullOrWhiteSpace(h.HealthMessage)
                                ? h.HealthMessage!
                                : fresh
                                    ? "heartbeat reported unhealthy"
                                    : $"last heartbeat {FormatAge(at - h.LastHeartbeatUtc)} ago";

                        var name = string.IsNullOrWhiteSpace(h.HostName)
                            ? h.WorkerKey
                            : $"{h.WorkerKey}@{h.HostName}";

                        return new DockerWorkerContainerDto(
                            ShortHeartbeatId(h.WorkerKey, h.HostName, h.ProcessId),
                            name,
                            state,
                            status,
                            color);
                    })
                    .ToList();

                var running = containers.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
                var total = containers.Count;
                var overall = running == 0 && total == 0
                    ? "stopped"
                    : running == total
                        ? "running"
                        : running == 0 ? "stopped" : "partial";
                var color = overall == "running" ? "green" : overall == "partial" ? "yellow" : "red";

                return new DockerWorkerServiceDto(
                    def.ServiceName,
                    def.DisplayName,
                    running,
                    total,
                    overall,
                    color,
                    containers);
            })
            .ToList();
    }


    private static async Task<IResult> GetWorkerDetailsAsync(
        string serviceName,
        IConfiguration configuration,
        ArgusDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DockerWorkerEndpoints");
        var def = ScalableServices.FirstOrDefault(d => string.Equals(d.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

        if (def is null)
        {
            return Results.BadRequest($"Unknown scalable worker service: {serviceName}");
        }

        var generatedAtUtc = DateTimeOffset.UtcNow;
        var workerKeys = def.WorkerKeys;
        var keySet = workerKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containers = new List<DockerWorkerContainerDto>();
        var dockerAvailable = true;
        string? dockerError = null;

        try
        {
            var allContainers = await DockerComposeWorkerScaler
                .GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            containers = allContainers
                .Where(c => string.Equals(c.ServiceName, def.ServiceName, StringComparison.OrdinalIgnoreCase))
                .Select(c => new DockerWorkerContainerDto(
                    DockerComposeWorkerScaler.ShortId(c.Id),
                    c.Name.TrimStart('/'),
                    c.State,
                    c.Status,
                    ContainerStateColor(c.State)))
                .ToList();
        }
        catch (Exception ex)
        {
            dockerAvailable = false;
            dockerError = ex.Message;
            logger.LogWarning(ex, "Docker worker detail query failed for {ServiceName}; returning heartbeat-based details.", serviceName);
        }

        var heartbeats = await db.WorkerHeartbeats
            .AsNoTracking()
            .Where(h => workerKeys.Contains(h.WorkerKey))
            .OrderBy(h => h.WorkerKey)
            .ThenBy(h => h.HostName)
            .Select(h => new
            {
                h.WorkerKey,
                h.HostName,
                h.ProcessId,
                h.Version,
                h.ActiveConsumerCount,
                h.IsHealthy,
                h.HealthMessage,
                h.LastHeartbeatUtc
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var heartbeatDtos = heartbeats
            .Select(h => new
            {
                h.WorkerKey,
                h.HostName,
                h.ProcessId,
                h.Version,
                h.ActiveConsumerCount,
                h.IsHealthy,
                h.HealthMessage,
                h.LastHeartbeatUtc,
                Health = BuildHeartbeatHealth(generatedAtUtc, h.IsHealthy, h.HealthMessage, h.LastHeartbeatUtc)
            })
            .ToList();

        var hostNames = heartbeats
            .Select(h => h.HostName)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var runningCount = containers.Count(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
        if (runningCount == 0)
        {
            runningCount = heartbeatDtos.Count(h => h.IsHealthy && h.LastHeartbeatUtc >= generatedAtUtc.AddMinutes(-2));
        }

        var totalCount = Math.Max(containers.Count, heartbeatDtos.Count);
        var overallState = runningCount == 0 && totalCount == 0
            ? "stopped"
            : runningCount == totalCount
                ? "running"
                : runningCount == 0 ? "stopped" : "partial";

        var recentHttpRaw = await db.HttpRequestQueue
            .AsNoTracking()
            .OrderByDescending(q => q.UpdatedAtUtc)
            .Take(300)
            .Select(q => new
            {
                q.Id,
                q.AssetId,
                q.TargetId,
                q.State,
                q.Method,
                q.RequestUrl,
                q.DomainKey,
                q.AttemptCount,
                q.MaxAttempts,
                q.CreatedAtUtc,
                q.UpdatedAtUtc,
                q.StartedAtUtc,
                q.CompletedAtUtc,
                q.LockedBy,
                q.LastHttpStatus,
                q.LastError
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var activeHttpRaw = recentHttpRaw
            .Where(q => q.State.Equals(HttpRequestQueueState.InFlight, StringComparison.OrdinalIgnoreCase))
            .Where(q => serviceName.Equals("worker-http-requester", StringComparison.OrdinalIgnoreCase) ||
                        IsLikelyWorkerLock(q.LockedBy, hostNames, keySet))
            .Take(25)
            .ToList();

        var recentHttpWorkRaw = recentHttpRaw
            .Where(q => serviceName.Equals("worker-http-requester", StringComparison.OrdinalIgnoreCase) ||
                        IsLikelyWorkerLock(q.LockedBy, hostNames, keySet))
            .Take(40)
            .ToList();

        var journalRaw = await db.BusJournal
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(500)
            .Select(e => new
            {
                e.Id,
                e.Direction,
                e.MessageType,
                e.ConsumerType,
                e.PayloadJson,
                e.OccurredAtUtc,
                e.HostName,
                e.Status,
                e.DurationMs,
                e.Error,
                e.MessageId
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var recentEventsRaw = journalRaw
            .Where(e => IsLikelyWorkerEvent(serviceName, keySet, hostNames, e.HostName, e.ConsumerType, e.MessageType, e.PayloadJson))
            .Take(40)
            .Select(e => new
            {
                e.Id,
                e.Direction,
                e.MessageType,
                e.ConsumerType,
                PayloadPreview = Truncate(e.PayloadJson, 512),
                e.OccurredAtUtc,
                e.HostName,
                e.Status,
                e.DurationMs,
                e.Error,
                e.MessageId,
                TargetId = TryExtractTargetId(e.PayloadJson)
            })
            .ToList();

        var outboxRaw = await db.OutboxMessages
            .AsNoTracking()
            .OrderByDescending(e => e.UpdatedAtUtc)
            .Take(250)
            .Select(e => new
            {
                e.Id,
                e.EventId,
                e.CorrelationId,
                e.CausationId,
                e.MessageType,
                e.PayloadJson,
                e.Producer,
                e.State,
                e.AttemptCount,
                e.CreatedAtUtc,
                e.UpdatedAtUtc,
                e.DispatchedAtUtc,
                e.LastError
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var recentOutboxRaw = outboxRaw
            .Where(e => IsLikelyWorkerMessage(serviceName, keySet, e.MessageType, e.PayloadJson, e.Producer))
            .Take(30)
            .Select(e => new
            {
                e.Id,
                e.EventId,
                e.CorrelationId,
                e.CausationId,
                e.MessageType,
                PayloadPreview = Truncate(e.PayloadJson, 512),
                e.Producer,
                e.State,
                e.AttemptCount,
                e.CreatedAtUtc,
                e.UpdatedAtUtc,
                e.DispatchedAtUtc,
                e.LastError,
                TargetId = TryExtractTargetId(e.PayloadJson)
            })
            .ToList();

        var targetIds = activeHttpRaw.Select(q => q.TargetId)
            .Concat(recentHttpWorkRaw.Select(q => q.TargetId))
            .Concat(recentEventsRaw.Select(e => e.TargetId ?? Guid.Empty))
            .Concat(recentOutboxRaw.Select(e => e.TargetId ?? Guid.Empty))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var targetMap = await BuildTargetNameMapAsync(db, targetIds, ct).ConfigureAwait(false);

        var activeHttp = activeHttpRaw
            .Select(q => new
            {
                q.Id,
                q.AssetId,
                q.TargetId,
                TargetRootDomain = TargetLabel(targetMap, q.TargetId),
                q.State,
                q.Method,
                q.RequestUrl,
                q.DomainKey,
                q.AttemptCount,
                q.MaxAttempts,
                q.CreatedAtUtc,
                q.UpdatedAtUtc,
                q.StartedAtUtc,
                q.CompletedAtUtc,
                q.LockedBy,
                q.LastHttpStatus,
                q.LastError
            })
            .ToList();

        var recentHttpWork = recentHttpWorkRaw
            .Select(q => new
            {
                q.Id,
                q.AssetId,
                q.TargetId,
                TargetRootDomain = TargetLabel(targetMap, q.TargetId),
                q.State,
                q.Method,
                q.RequestUrl,
                q.DomainKey,
                q.AttemptCount,
                q.MaxAttempts,
                q.CreatedAtUtc,
                q.UpdatedAtUtc,
                q.StartedAtUtc,
                q.CompletedAtUtc,
                q.LockedBy,
                q.LastHttpStatus,
                q.LastError
            })
            .ToList();

        var recentEvents = recentEventsRaw
            .Select(e => new
            {
                e.Id,
                e.Direction,
                e.MessageType,
                e.ConsumerType,
                e.PayloadPreview,
                e.OccurredAtUtc,
                e.HostName,
                e.Status,
                e.DurationMs,
                e.Error,
                e.MessageId,
                e.TargetId,
                TargetRootDomain = e.TargetId is Guid id ? TargetLabel(targetMap, id) : ""
            })
            .ToList();

        var recentOutbox = recentOutboxRaw
            .Select(e => new
            {
                e.Id,
                e.EventId,
                e.CorrelationId,
                e.CausationId,
                e.MessageType,
                e.PayloadPreview,
                e.Producer,
                e.State,
                e.AttemptCount,
                e.CreatedAtUtc,
                e.UpdatedAtUtc,
                e.DispatchedAtUtc,
                e.LastError,
                e.TargetId,
                TargetRootDomain = e.TargetId is Guid id ? TargetLabel(targetMap, id) : ""
            })
            .ToList();

        var summaries = new[]
        {
            new { Label = "Processes with fresh heartbeats", Value = heartbeatDtos.Count(h => h.LastHeartbeatUtc >= generatedAtUtc.AddMinutes(-2)).ToString(CultureInfo.InvariantCulture) },
            new { Label = "Active consumers", Value = heartbeatDtos.Sum(h => h.ActiveConsumerCount).ToString(CultureInfo.InvariantCulture) },
            new { Label = "Active HTTP requests", Value = activeHttp.Count.ToString(CultureInfo.InvariantCulture) },
            new { Label = "Recent matching bus events", Value = recentEvents.Count.ToString(CultureInfo.InvariantCulture) },
            new { Label = "Recent queued/dispatched outbox events", Value = recentOutbox.Count.ToString(CultureInfo.InvariantCulture) },
            new { Label = "Recent failed HTTP rows", Value = recentHttpWork.Count(q => q.State.Equals(HttpRequestQueueState.Failed, StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture) }
        };

        return Results.Ok(new
        {
            ServiceName = def.ServiceName,
            def.DisplayName,
            GeneratedAtUtc = generatedAtUtc,
            DockerAvailable = dockerAvailable,
            DockerError = dockerError,
            RunningCount = runningCount,
            TotalCount = totalCount,
            OverallState = overallState,
            Containers = containers,
            Heartbeats = heartbeatDtos,
            ActiveHttpRequests = activeHttp,
            RecentHttpWork = recentHttpWork,
            RecentEvents = recentEvents,
            RecentOutbox = recentOutbox,
            Summaries = summaries
        });
    }

    private static async Task<IResult> ScaleDockerWorkerAsync(
        string serviceName,
        DockerWorkerScaleRequest body,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DockerWorkerEndpoints");
        var def = ScalableServices.FirstOrDefault(d => string.Equals(d.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

        if (def is null)
        {
            return Results.BadRequest($"Unknown scalable worker service: {serviceName}");
        }

        if (body.DesiredCount < 0 || body.DesiredCount > 50)
        {
            return Results.BadRequest("desiredCount must be between 0 and 50.");
        }

        try
        {
            var before = await DockerComposeWorkerScaler.GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);
            var previousCount = before.Count(c =>
                string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) &&
                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

            await DockerComposeWorkerScaler.ScaleWorkerAsync(serviceName, body.DesiredCount, configuration, logger, ct)
                .ConfigureAwait(false);

            await Task.Delay(1000, ct).ConfigureAwait(false);

            var after = await DockerComposeWorkerScaler.GetComposeContainersAsync(configuration, logger, ct)
                .ConfigureAwait(false);
            var actualCount = after.Count(c =>
                string.Equals(c.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) &&
                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));

            var message = body.DesiredCount == previousCount
                ? $"{serviceName} already at {previousCount} running container(s); no change requested."
                : $"{serviceName} requested scale from {previousCount} to {body.DesiredCount}; {actualCount} running container(s) observed after scaling.";

            return Results.Ok(new DockerWorkerScaleResult(
                serviceName,
                previousCount,
                body.DesiredCount,
                actualCount,
                message));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker scale failed for {ServiceName}.", serviceName);
            return Results.Problem(
                title: "Docker scale failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }


    private static bool IsLikelyWorkerLock(string? lockedBy, IReadOnlyCollection<string> hostNames, IReadOnlySet<string> workerKeys)
    {
        if (string.IsNullOrWhiteSpace(lockedBy))
        {
            return false;
        }

        if (hostNames.Any(h => lockedBy.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return workerKeys.Any(k => lockedBy.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyWorkerEvent(
        string serviceName,
        IReadOnlySet<string> workerKeys,
        IReadOnlyCollection<string> hostNames,
        string hostName,
        string? consumerType,
        string messageType,
        string payloadJson)
    {
        if (!string.IsNullOrWhiteSpace(hostName) &&
            hostNames.Any(h => hostName.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsLikelyWorkerMessage(serviceName, workerKeys, messageType, payloadJson, consumerType);
    }

    private static bool IsLikelyWorkerMessage(
        string serviceName,
        IReadOnlySet<string> workerKeys,
        string messageType,
        string payloadJson,
        string? consumerOrProducer)
    {
        var haystacks = new[]
        {
            messageType ?? "",
            consumerOrProducer ?? "",
            payloadJson.Length <= 2048 ? payloadJson : ""
        };

        if (workerKeys.Any(key => haystacks.Any(h => h.Contains(key, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        foreach (var term in WorkerDetailTerms(serviceName))
        {
            if (haystacks.Any(h => h.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] WorkerDetailTerms(string serviceName) =>
        serviceName.ToLowerInvariant() switch
        {
            "worker-spider" => ["spider", "crawl", "httpresponsedownloaded", "rootspider", "url"],
            "worker-http-requester" => ["http", "request", "response", "download"],
            "worker-enum" => ["enum", "subdomain", "subfinder", "amass"],
            "worker-portscan" => ["port", "scan", "openport"],
            "worker-highvalue" => ["highvalue", "secret", "finding", "regex", "path"],
            "worker-techid" => ["tech", "technology", "fingerprint", "detection"],
            _ => [serviceName]
        };

    private static string BuildHeartbeatHealth(DateTimeOffset now, bool isHealthy, string? message, DateTimeOffset lastHeartbeatUtc)
    {
        var age = now - lastHeartbeatUtc;
        var ageLabel = FormatAge(age);

        if (isHealthy && age <= TimeSpan.FromMinutes(2))
        {
            return $"healthy, heartbeat {ageLabel} ago";
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return $"{message} ({ageLabel} ago)";
        }

        return age > TimeSpan.FromMinutes(2)
            ? $"stale, heartbeat {ageLabel} ago"
            : "unhealthy";
    }

    private static Guid? TryExtractTargetId(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return FindGuidProperty(document.RootElement, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? FindGuidProperty(JsonElement element, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("targetId", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("TargetId", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(property.Value.GetString(), out var id))
                    {
                        return id;
                    }
                }

                var nested = FindGuidProperty(property.Value, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindGuidProperty(item, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static async Task<Dictionary<Guid, string>> BuildTargetNameMapAsync(
        ArgusDbContext db,
        IReadOnlyCollection<Guid> targetIds,
        CancellationToken ct)
    {
        if (targetIds.Count == 0)
        {
            return [];
        }

        var map = await db.Targets
            .AsNoTracking()
            .Where(t => targetIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.RootDomain, ct)
            .ConfigureAwait(false);

        var missing = targetIds.Where(id => !map.ContainsKey(id)).ToArray();
        if (missing.Length == 0)
        {
            return map;
        }

        var fallbackRows = await db.Assets
            .AsNoTracking()
            .Where(a => missing.Contains(a.TargetId))
            .OrderBy(a => a.Depth)
            .ThenBy(a => a.DiscoveredAtUtc)
            .Select(a => new
            {
                a.TargetId,
                a.RawValue,
                a.DisplayName,
                a.CanonicalKey
            })
            .Take(2000)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var group in fallbackRows.GroupBy(a => a.TargetId))
        {
            var label = group
                .Select(a => FirstNonEmpty(a.DisplayName, a.RawValue, a.CanonicalKey))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            if (!string.IsNullOrWhiteSpace(label))
            {
                map[group.Key] = NormalizeTargetLabel(label);
            }
        }

        return map;
    }

    private static string TargetLabel(IReadOnlyDictionary<Guid, string> targetMap, Guid id) =>
        targetMap.TryGetValue(id, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : id.ToString();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string NormalizeTargetLabel(string value)
    {
        var label = value.Trim().TrimEnd('/');

        if (Uri.TryCreate(label, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            label = uri.Host;
        }

        return label.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private static string ContainerStateColor(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ? "green" :
        state.Equals("exited", StringComparison.OrdinalIgnoreCase) ? "red" :
        state.Equals("created", StringComparison.OrdinalIgnoreCase) ? "yellow" :
        "gray";

    private static string ShortHeartbeatId(string workerKey, string hostName, int processId)
    {
        var raw = $"{workerKey}:{hostName}:{processId}";
        var hash = raw.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return $"{Math.Abs(hash):x8}";
    }

    private static string FormatAge(TimeSpan age)
    {
        age = age < TimeSpan.Zero ? TimeSpan.Zero : age;

        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        return $"{Math.Max(1, (int)age.TotalHours)}h";
    }

    private sealed record WorkerServiceDefinition(
        string ServiceName,
        string DisplayName,
        string[] WorkerKeys);
}
