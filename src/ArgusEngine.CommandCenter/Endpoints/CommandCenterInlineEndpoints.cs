using Amazon;
using Amazon.ECS;
using Amazon.Runtime;
using System.Net.Http;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.FileStore;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Hubs;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.CommandCenter.Realtime;
using ArgusEngine.CommandCenter.Services.Aws;
using ArgusEngine.CommandCenter.Services.Targets;
using ArgusEngine.CommandCenter.Services.Workers;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using AssignPublicIp = Amazon.ECS.AssignPublicIp;
using AssetAdmissionStage = ArgusEngine.Contracts.AssetAdmissionStage;
using AssetKind = ArgusEngine.Contracts.AssetKind;
using AwsVpcConfiguration = Amazon.ECS.Model.AwsVpcConfiguration;
using CreateServiceRequest = Amazon.ECS.Model.CreateServiceRequest;
using DeploymentConfiguration = Amazon.ECS.Model.DeploymentConfiguration;
using DescribeServicesRequest = Amazon.ECS.Model.DescribeServicesRequest;
using EcsService = Amazon.ECS.Model.Service;
using ListTaskDefinitionsRequest = Amazon.ECS.Model.ListTaskDefinitionsRequest;
using LaunchType = Amazon.ECS.LaunchType;
using NetworkConfiguration = Amazon.ECS.Model.NetworkConfiguration;
using SortOrder = Amazon.ECS.SortOrder;
using TaskDefinitionStatus = Amazon.ECS.TaskDefinitionStatus;
using UpdateServiceRequest = Amazon.ECS.Model.UpdateServiceRequest;
using UrlFetchSnapshot = ArgusEngine.Application.Assets.UrlFetchSnapshot;

namespace ArgusEngine.CommandCenter.Endpoints;

internal static class CommandCenterInlineEndpoints
{
    public static IEndpointRouteBuilder MapCommandCenterInlineEndpoints(this IEndpointRouteBuilder app)
    {


        app.MapGet(
                "/api/workers",
                async (NightmareDbContext db, WorkerScaleDefinitionProvider workerDefinitions, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var persisted = await db.WorkerSwitches.AsNoTracking()
                        .Select(w => new WorkerSwitchDto(w.WorkerKey, w.IsEnabled, w.UpdatedAtUtc))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var rows = workerDefinitions.RequiredWorkerKeys
                        .Select(key => persisted.FirstOrDefault(w => w.WorkerKey == key) ?? new WorkerSwitchDto(key, true, now))
                        .Concat(persisted.Where(w => !workerDefinitions.RequiredWorkerKeys.Contains(w.WorkerKey, StringComparer.Ordinal)))
                        .OrderBy(w => w.WorkerKey, StringComparer.Ordinal)
                        .ToList();
                    return Results.Ok(rows);
                })
            .WithName("ListWorkers");

        app.MapGet(
                "/api/workers/capabilities",
                () =>
                {
                    var rows = new[]
                    {
                        new WorkerCapabilityDto(WorkerKeys.Gatekeeper, "Gatekeeper", "v1", true, true, false, false),
                        new WorkerCapabilityDto(WorkerKeys.Spider, "Spider HTTP Queue", "v1", false, true, true, false),
                        new WorkerCapabilityDto(WorkerKeys.Enumeration, "Enumeration", "v1", true, true, true, false),
                        new WorkerCapabilityDto(WorkerKeys.PortScan, "Port Scan", "v1", true, false, true, true),
                        new WorkerCapabilityDto(WorkerKeys.HighValueRegex, "High Value Regex", "v1", true, false, false, false),
                        new WorkerCapabilityDto(WorkerKeys.HighValuePaths, "High Value Paths", "v1", true, true, false, false),
                        new WorkerCapabilityDto(WorkerKeys.TechnologyIdentification, "Technology Identification", "v1", false, true, true, false),
                    };
                    return Results.Ok(rows);
                })
            .WithName("WorkerCapabilities");

        app.MapGet(
                "/api/workers/health",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var since1 = now.AddHours(-1);
                    var since24 = now.AddHours(-24);

                    var toggles = await db.WorkerSwitches.AsNoTracking()
                        .ToDictionaryAsync(w => w.WorkerKey, w => w.IsEnabled, ct)
                        .ConfigureAwait(false);

                    var consumeRows = await db.BusJournal.AsNoTracking()
                        .Where(e => e.Direction == "Consume" && e.ConsumerType != null && e.OccurredAtUtc >= since24)
                        .Select(e => new { e.ConsumerType, e.OccurredAtUtc })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var byKind = consumeRows
                        .Select(r => new { Kind = WorkerConsumerKindResolver.KindFromConsumerType(r.ConsumerType), r.OccurredAtUtc })
                        .Where(r => !string.IsNullOrWhiteSpace(r.Kind))
                        .GroupBy(r => r.Kind!)
                        .ToDictionary(
                            g => g.Key,
                            g => new
                            {
                                Last = g.Max(x => x.OccurredAtUtc),
                                Last1h = g.LongCount(x => x.OccurredAtUtc >= since1),
                                Last24h = (long)g.Count(),
                            },
                            StringComparer.Ordinal);

                    var spiderRows = await db.HttpRequestQueue.AsNoTracking()
                        .Where(q => q.StartedAtUtc != null && q.StartedAtUtc >= since24)
                        .Select(q => q.StartedAtUtc!.Value)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    if (spiderRows.Count > 0)
                    {
                        byKind[WorkerKeys.Spider] = new
                        {
                            Last = spiderRows.Max(),
                            Last1h = spiderRows.LongCount(t => t >= since1),
                            Last24h = (long)spiderRows.Count,
                        };
                    }

                    var keys = new[]
                    {
                        WorkerKeys.Gatekeeper,
                        WorkerKeys.Spider,
                        WorkerKeys.Enumeration,
                        WorkerKeys.PortScan,
                        WorkerKeys.HighValueRegex,
                        WorkerKeys.HighValuePaths,
                        WorkerKeys.TechnologyIdentification,
                    };

                    var rows = keys.Select(
                            key =>
                            {
                                var enabled = toggles.GetValueOrDefault(key, true);
                                var has = byKind.TryGetValue(key, out var stats);
                                var last = has ? stats!.Last : (DateTimeOffset?)null;
                                var c1 = has ? stats!.Last1h : 0;
                                var c24 = has ? stats!.Last24h : 0;
                                var healthy = !enabled || c1 > 0 || (last is not null && (now - last.Value) <= TimeSpan.FromMinutes(15));
                                var reason = !enabled
                                    ? "worker toggle is disabled"
                                    : healthy
                                        ? (key == WorkerKeys.Spider ? "spider processed HTTP queue rows recently" : "worker consumed events recently")
                                        : (key == WorkerKeys.Spider ? "spider has no recent HTTP queue activity" : "worker has no recent consume activity");
                                return new WorkerHealthDto(key, enabled, last, c1, c24, healthy, reason);
                            })
                        .ToList();

                    return Results.Ok(rows);
                })
            .WithName("WorkerHealth");

        app.MapGet(
                "/api/workers/activity",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var snap = await WorkerActivityQuery.BuildSnapshotAsync(db, ct).ConfigureAwait(false);
                    return Results.Ok(snap);
                })
            .WithName("WorkerActivity");

        app.MapGet(
                "/api/workers/scale-overrides",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var rows = await db.WorkerScaleTargets.AsNoTracking()
                        .OrderBy(t => t.ScaleKey)
                        .Select(t => new WorkerScaleOverrideDto(t.ScaleKey, t.DesiredCount))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("WorkerScaleOverrides");

        app.MapGet(
                "/api/workers/scale",
                async (
                    NightmareDbContext db,
                    IConfiguration configuration,
                    WorkerScaleDefinitionProvider workerDefinitions,
                    AwsRegionResolver regionResolver,
                    EcsServiceNameResolver serviceNameResolver,
                    EcsWorkerServiceManager ecsWorkerServices,
                    CancellationToken ct) =>
                {
                    var overrides = await db.WorkerScaleTargets.AsNoTracking()
                        .ToDictionaryAsync(t => t.ScaleKey, t => t, StringComparer.Ordinal, ct)
                        .ConfigureAwait(false);

                    var definitions = workerDefinitions.RequiredWorkerKeys
                        .Select(key => new { WorkerKey = key, Target = workerDefinitions.GetScaleTargetForWorkerKey(key) })
                        .Where(x => x.Target is not null)
                        .Select(
                            x =>
                            {
                                var target = x.Target!;
                                return new
                                {
                                    x.WorkerKey,
                                    target.ScaleKey,
                                    ServiceName = serviceNameResolver.ServiceNameForScaleKey(target.ScaleKey, target.DefaultServiceName),
                                };
                            })
                        .ToList();

                    Dictionary<string, EcsService> services = [];
                    var ecsConfigured = false;
                    var status = "ECS region is not configured and could not be inferred from EC2 metadata.";
                    var region = await regionResolver.ResolveAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(region))
                    {
                        try
                        {
                            services = await ecsWorkerServices.DescribeServicesAsync(definitions.Select(d => d.ServiceName), ct).ConfigureAwait(false);
                            ecsConfigured = true;
                            status = "ECS service state loaded.";
                        }
                        catch (AmazonECSException ex)
                        {
                            status = $"ECS API error: {ex.Message}";
                        }
                        catch (AmazonServiceException ex)
                        {
                            status = $"AWS API error: {ex.Message}";
                        }
                        catch (Exception ex)
                        {
                            status = $"ECS service state unavailable: {ex.Message}";
                        }
                    }

                    if (ecsConfigured)
                    {
                        foreach (var definition in definitions)
                        {
                            if (overrides.ContainsKey(definition.ScaleKey))
                                continue;
                            if (!services.TryGetValue(definition.ServiceName, out var service))
                                continue;

                            var fallback = workerDefinitions.DefaultWorkerScalingSetting(definition.ScaleKey);
                            var currentDesired = Math.Max(0, service.DesiredCount.GetValueOrDefault());
                            if (currentDesired >= fallback.MinTasks)
                                continue;

                            try
                            {
                                await ecsWorkerServices.UpdateDesiredCountAsync(definition.ServiceName, fallback.MinTasks, ct)
                                    .ConfigureAwait(false);
                                service.DesiredCount = fallback.MinTasks;
                                status = "ECS service state loaded; default minimum worker counts reconciled.";
                            }
                            catch
                            {
                                // Keep the read-only state. Manual Set still returns the detailed ECS error.
                            }
                        }
                    }

                    var rows = definitions
                        .Select(
                            definition =>
                            {
                                overrides.TryGetValue(definition.ScaleKey, out var manual);
                                services.TryGetValue(definition.ServiceName, out var service);
                                var fallback = workerDefinitions.DefaultWorkerScalingSetting(definition.ScaleKey);
                                var displayedDesired = service?.DesiredCount;
                                var displayedManual = manual?.DesiredCount ?? (service is null ? (int?)fallback.MinTasks : null);
                                return new WorkerScaleTargetDto(
                                    definition.WorkerKey,
                                    definition.ScaleKey,
                                    definition.ServiceName,
                                    displayedDesired,
                                    service?.RunningCount,
                                    service?.PendingCount,
                                    displayedManual,
                                    ecsConfigured && service is not null,
                                    service is null ? status : "ECS service state loaded.");
                            })
                        .ToList();

                    return Results.Ok(rows);
                })
            .WithName("WorkerScaleTargets");

        app.MapGet(
                "/api/workers/scaling-settings",
                async (NightmareDbContext db, WorkerScaleDefinitionProvider workerDefinitions, CancellationToken ct) =>
                {
                    var persisted = await db.WorkerScalingSettings.AsNoTracking()
                        .ToDictionaryAsync(s => s.ScaleKey, StringComparer.Ordinal, ct)
                        .ConfigureAwait(false);

                    var rows = workerDefinitions.WorkerScaleDefinitions
                        .Select(
                            definition =>
                            {
                                var fallback = workerDefinitions.DefaultWorkerScalingSetting(definition.ScaleKey);
                                return persisted.TryGetValue(definition.ScaleKey, out var row)
                                    ? new WorkerScalingSettingsDto(
                                        definition.ScaleKey,
                                        definition.DisplayName,
                                        row.MinTasks,
                                        row.MaxTasks,
                                        row.TargetBacklogPerTask,
                                        row.UpdatedAtUtc)
                                    : fallback;
                            })
                        .ToList();

                    return Results.Ok(rows);
                })
            .WithName("WorkerScalingSettings");

        app.MapPut(
                "/api/workers/scaling-settings/{scaleKey}",
                async (
                    string scaleKey,
                    WorkerScalingSettingsPatchDto body,
                    NightmareDbContext db,
                    WorkerScaleDefinitionProvider workerDefinitions,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    if (!workerDefinitions.WorkerScaleDefinitions.Any(d => string.Equals(d.ScaleKey, scaleKey, StringComparison.Ordinal)))
                        return Results.BadRequest($"Unknown worker scale key: {scaleKey}");

                    if (body.MinTasks < 0)
                        return Results.BadRequest("minTasks must be greater than or equal to zero.");
                    if (body.MaxTasks < body.MinTasks)
                        return Results.BadRequest("maxTasks must be greater than or equal to minTasks.");
                    if (body.TargetBacklogPerTask <= 0)
                        return Results.BadRequest("targetBacklogPerTask must be greater than zero.");

                    var now = DateTimeOffset.UtcNow;
                    var row = await db.WorkerScalingSettings.FirstOrDefaultAsync(s => s.ScaleKey == scaleKey, ct).ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new WorkerScalingSetting { ScaleKey = scaleKey };
                        db.WorkerScalingSettings.Add(row);
                    }

                    row.MinTasks = body.MinTasks;
                    row.MaxTasks = body.MaxTasks;
                    row.TargetBacklogPerTask = body.TargetBacklogPerTask;
                    row.UpdatedAtUtc = now;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    await hub.Clients.All.SendAsync(
                            DiscoveryHubEvents.DomainEvent,
                            new LiveUiEventDto("WorkerScalingSettingsChanged", null, null, "workers", $"{scaleKey} scaling settings updated", now),
                            cancellationToken: ct)
                        .ConfigureAwait(false);

                    var displayName = workerDefinitions.WorkerScaleDefinitions.First(d => d.ScaleKey == scaleKey).DisplayName;
                    return Results.Ok(new WorkerScalingSettingsDto(scaleKey, displayName, row.MinTasks, row.MaxTasks, row.TargetBacklogPerTask, now));
                })
            .WithName("UpdateWorkerScalingSettings");

        app.MapGet(
                "/api/ops/ecs-status",
                async (
                    IConfiguration configuration,
                    WorkerScaleDefinitionProvider workerDefinitions,
                    AwsRegionResolver regionResolver,
                    EcsServiceNameResolver serviceNameResolver,
                    EcsWorkerServiceManager ecsWorkerServices,
                    CancellationToken ct) =>
                {
                    var at = DateTimeOffset.UtcNow;
                    var region = await regionResolver.ResolveAsync(ct).ConfigureAwait(false);
                    var cluster = configuration["ECS_CLUSTER"] ?? "nightmare-v2";
                    if (string.IsNullOrWhiteSpace(region))
                    {
                        return Results.Ok(
                            new EcsRuntimeStatusDto(
                                at,
                                false,
                                cluster,
                                "unknown",
                                "gray",
                                "AWS region is not configured and could not be inferred from EC2 metadata.",
                                []));
                    }

                    var definitions = workerDefinitions.WorkerScaleDefinitions
                        .Select(d => new
                        {
                            d.ScaleKey,
                            d.DisplayName,
                            ServiceName = serviceNameResolver.ServiceNameForScaleKey(d.ScaleKey, d.DefaultServiceName),
                        })
                        .ToList();

                    try
                    {
                        var services = await ecsWorkerServices.DescribeServicesAsync(definitions.Select(d => d.ServiceName), ct).ConfigureAwait(false);
                        var rows = definitions
                            .Select(
                                definition =>
                                {
                                    services.TryGetValue(definition.ServiceName, out var service);
                                    if (service is null)
                                    {
                                        return new EcsServiceStatusDto(
                                            definition.ScaleKey,
                                            definition.ServiceName,
                                            "missing",
                                            "-",
                                            null,
                                            0,
                                            0,
                                            0,
                                            "-",
                                            "service not found",
                                            "red");
                                    }

                                    var deployment = service.Deployments
                                        .OrderByDescending(d => d.UpdatedAt)
                                        .FirstOrDefault();
                                    var status = service.Status ?? "unknown";
                                    var deploymentStatus = deployment?.Status ?? "unknown";
                                    var desiredCount = Math.Max(0, service.DesiredCount.GetValueOrDefault());
                                    var runningCount = Math.Max(0, service.RunningCount.GetValueOrDefault());
                                    var pendingCount = Math.Max(0, service.PendingCount.GetValueOrDefault());
                                    var color = status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
                                        ? runningCount >= desiredCount && pendingCount == 0 ? "green" : "yellow"
                                        : "red";
                                    var taskDefinition = deployment?.TaskDefinition ?? service.TaskDefinition ?? "-";

                                    return new EcsServiceStatusDto(
                                        definition.ScaleKey,
                                        service.ServiceName,
                                        status,
                                        EcsWorkerServiceManager.ExtractTaskDefinitionVersion(taskDefinition),
                                        deployment?.CreatedAt is { } created ? new DateTimeOffset(created, TimeSpan.Zero) : null,
                                        desiredCount,
                                        runningCount,
                                        pendingCount,
                                        taskDefinition,
                                        deploymentStatus,
                                        color);
                                })
                            .ToList();

                        var overallColor = rows.Any(r => r.Color == "red") ? "red" : rows.Any(r => r.Color == "yellow") ? "yellow" : "green";
                        var overallStatus = overallColor == "green" ? "healthy" : overallColor == "yellow" ? "degraded" : "critical";
                        return Results.Ok(new EcsRuntimeStatusDto(at, true, cluster, overallStatus, overallColor, null, rows));
                    }
                    catch (Exception ex)
                    {
                        return Results.Ok(new EcsRuntimeStatusDto(at, false, cluster, "unknown", "gray", ex.Message, []));
                    }
                })
            .WithName("EcsRuntimeStatus");

        app.MapGet(
                "/api/ops/snapshot",
                async (NightmareDbContext db, IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
                {
                    var snap = await OpsSnapshotBuilder.BuildAsync(db, httpFactory, configuration, ct).ConfigureAwait(false);
                    return Results.Ok(snap);
                })
            .WithName("OpsSnapshot");

        app.MapGet(
                "/api/ops/rabbit-queues",
                async (IHttpClientFactory httpFactory, IConfiguration configuration, CancellationToken ct) =>
                {
                    var (queues, _) = await OpsSnapshotBuilder.LoadRabbitQueuesAsync(httpFactory, configuration, ct).ConfigureAwait(false);
                    return Results.Ok(queues);
                })
            .WithName("OpsRabbitQueues");


        app.MapGet(
                "/api/ops/overview",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var totalTargets = await db.Targets.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
                    var totalAssetsConfirmed = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed, ct)
                        .ConfigureAwait(false);
                    var totalUrls = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.Kind == AssetKind.Url, ct)
                        .ConfigureAwait(false);

                    var urlsFromFetchedPages = await db.Assets.AsNoTracking()
                        .LongCountAsync(
                            a => a.Kind == AssetKind.Url
                                && a.DiscoveredBy == "spider-worker"
                                && EF.Functions.Like(a.DiscoveryContext, "Spider: link extracted from fetched page %"),
                            ct)
                        .ConfigureAwait(false);

                    var urlsFromScripts = await db.Assets.AsNoTracking()
                        .LongCountAsync(
                            a => a.Kind == AssetKind.Url
                                && a.DiscoveredBy == "spider-worker"
                                && (EF.Functions.ILike(a.DiscoveryContext, "%.js%")
                                    || EF.Functions.ILike(a.DiscoveryContext, "%javascript%")),
                            ct)
                        .ConfigureAwait(false);

                    var urlsGuessedWithWordlist = await db.Assets.AsNoTracking()
                        .LongCountAsync(
                            a => a.Kind == AssetKind.Url
                                && EF.Functions.ILike(a.DiscoveredBy, "hvpath:%"),
                            ct)
                        .ConfigureAwait(false);

                    var subdomainsDiscovered = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.Kind == AssetKind.Subdomain, ct)
                        .ConfigureAwait(false);
                    var lastAssetCreatedAt = await db.Assets.AsNoTracking()
                        .OrderByDescending(a => a.DiscoveredAtUtc)
                        .Select(a => (DateTimeOffset?)a.DiscoveredAtUtc)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    var lastWorkerEventPublishedAt = await db.BusJournal.AsNoTracking()
                        .Where(e => e.Direction == "Publish")
                        .OrderByDescending(e => e.OccurredAtUtc)
                        .Select(e => (DateTimeOffset?)e.OccurredAtUtc)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);
                    var queuedHttpAssets = await db.Assets.AsNoTracking()
                        .LongCountAsync(a => a.Kind == AssetKind.Url && a.LifecycleStatus == AssetLifecycleStatus.Queued, ct)
                        .ConfigureAwait(false);

                    var domainCounts = await db.Assets.AsNoTracking()
                        .Join(db.Targets.AsNoTracking(), a => a.TargetId, t => t.Id, (_, t) => t.RootDomain)
                        .GroupBy(d => d)
                        .Select(g => new { RootDomain = g.Key, Count = g.LongCount() })
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    var top = domainCounts
                        .OrderByDescending(x => x.Count)
                        .ThenBy(x => x.RootDomain, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    var domains10OrMore = domainCounts.LongCount(x => x.Count >= 10);
                    var domains10OrFewer = domainCounts.LongCount(x => x.Count < 10);

                    return Results.Ok(
                        new OpsOverviewDto(
                            totalTargets,
                            totalAssetsConfirmed,
                            totalUrls,
                            urlsFromFetchedPages,
                            urlsFromScripts,
                            urlsGuessedWithWordlist,
                            top?.RootDomain,
                            top?.Count ?? 0,
                            domains10OrMore,
                            domains10OrFewer,
                            subdomainsDiscovered,
                            lastAssetCreatedAt,
                            lastWorkerEventPublishedAt,
                            queuedHttpAssets));
                })
            .WithName("OpsOverview");

        app.MapGet(
                "/api/ops/reliability-slo",
                async (NightmareDbContext db, CancellationToken ct) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var since = now.AddHours(-1);

                    var publishes = await db.BusJournal.AsNoTracking()
                        .LongCountAsync(e => e.Direction == "Publish" && e.OccurredAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var consumes = await db.BusJournal.AsNoTracking()
                        .LongCountAsync(e => e.Direction == "Consume" && e.OccurredAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var successRate = publishes <= 0 ? 1m : Math.Min(1m, consumes / (decimal)publishes);

                    var queued = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Queued, ct)
                        .ConfigureAwait(false);
                    var readyRetry = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now, ct)
                        .ConfigureAwait(false);
                    var backlog = queued + readyRetry;
                    var completed = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Succeeded && q.CompletedAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var failedLastHour = await db.HttpRequestQueue.AsNoTracking()
                        .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= since, ct)
                        .ConfigureAwait(false);
                    var oldestQueuedAt = await db.HttpRequestQueue.AsNoTracking()
                        .Where(q => q.State == HttpRequestQueueState.Queued
                            || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now))
                        .OrderBy(q => q.CreatedAtUtc)
                        .Select(q => (DateTimeOffset?)q.CreatedAtUtc)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false);

                    var apiReady = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
                    return Results.Ok(
                        new ReliabilitySloSnapshotDto(
                            now,
                            publishes,
                            consumes,
                            successRate,
                            backlog,
                            oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds,
                            completed,
                            failedLastHour,
                            apiReady));
                })
            .WithName("ReliabilitySloSnapshot");

        app.MapGet(
                "/api/ops/docker-status",
                async (CancellationToken ct) =>
                {
                    var snapshot = await DockerRuntimeStatusBuilder.BuildAsync(ct).ConfigureAwait(false);
                    return Results.Ok(snapshot);
                })
            .WithName("DockerRuntimeStatus");

        app.MapPut(
                "/api/workers/{key}",
                async (string key, WorkerPatchRequest body, NightmareDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
                {
                    var row = await db.WorkerSwitches.FirstOrDefaultAsync(w => w.WorkerKey == key, ct).ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new WorkerSwitch { WorkerKey = key };
                        db.WorkerSwitches.Add(row);
                    }
                    row.IsEnabled = body.Enabled;
                    row.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await hub.Clients.All.SendAsync(
                            DiscoveryHubEvents.DomainEvent,
                            new LiveUiEventDto(
                                "WorkerToggleChanged",
                                null,
                                null,
                                "workers",
                                $"{row.WorkerKey} {(row.IsEnabled ? "enabled" : "disabled")}",
                                row.UpdatedAtUtc),
                            cancellationToken: ct)
                        .ConfigureAwait(false);
                    return Results.NoContent();
                })
            .WithName("PatchWorker");

        app.MapPut(
                "/api/workers/{key}/scale",
                async (
                    string key,
                    WorkerScalePatchRequest body,
                    NightmareDbContext db,
                    IConfiguration configuration,
                    WorkerScaleDefinitionProvider workerDefinitions,
                    AwsRegionResolver regionResolver,
                    EcsServiceNameResolver serviceNameResolver,
                    EcsWorkerServiceManager ecsWorkerServices,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    var target = workerDefinitions.GetScaleTargetForWorkerKey(key);
                    if (target is null)
                        return Results.BadRequest($"{key} does not map to a scalable ECS worker service.");

                    var maxDesiredCount = configuration.GetValue<int?>("Nightmare:WorkerScaling:MaxDesiredCount") ?? 100;
                    if (body.DesiredCount < 0 || body.DesiredCount > maxDesiredCount)
                        return Results.BadRequest($"desiredCount must be between 0 and {maxDesiredCount}.");

                    var scaleKey = target.ScaleKey;
                    var now = DateTimeOffset.UtcNow;
                    var row = await db.WorkerScaleTargets.FirstOrDefaultAsync(t => t.ScaleKey == scaleKey, ct).ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new WorkerScaleTarget { ScaleKey = scaleKey };
                        db.WorkerScaleTargets.Add(row);
                    }

                    row.DesiredCount = body.DesiredCount;
                    row.UpdatedAtUtc = now;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    var serviceName = serviceNameResolver.ServiceNameForScaleKey(scaleKey, target.DefaultServiceName);
                    var ecsUpdated = false;
                    var message = "Manual worker count saved. ECS was not updated because AWS region is not configured and could not be inferred from EC2 metadata.";
                    var region = await regionResolver.ResolveAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(region))
                    {
                        try
                        {
                            var result = await ecsWorkerServices.EnsureWorkerServiceDesiredCountAsync(scaleKey, serviceName, body.DesiredCount, ct)
                                .ConfigureAwait(false);
                            ecsUpdated = result.Changed;
                            message = result.Action switch
                            {
                                "created" => $"Created {serviceName} and set desired count to {body.DesiredCount}.",
                                "updated" => $"Set {serviceName} desired count to {body.DesiredCount}.",
                                _ => $"Manual worker count saved for {serviceName}.",
                            };
                        }
                        catch (AmazonECSException ex)
                        {
                            message = $"Manual worker count saved, but ECS update/create failed: {ex.Message}";
                        }
                        catch (AmazonServiceException ex)
                        {
                            message = $"Manual worker count saved, but AWS update failed: {ex.Message}";
                        }
                        catch (Exception ex)
                        {
                            message = $"Manual worker count saved, but ECS update/create failed: {ex.Message}";
                        }
                    }

                    await hub.Clients.All.SendAsync(
                            DiscoveryHubEvents.DomainEvent,
                            new LiveUiEventDto(
                                "WorkerScaleChanged",
                                null,
                                null,
                                "workers",
                                $"{scaleKey} desired count set to {body.DesiredCount}",
                                now),
                            cancellationToken: ct)
                        .ConfigureAwait(false);

                    return Results.Ok(new WorkerScaleUpdateResult(key, scaleKey, body.DesiredCount, ecsUpdated, message));
                })
            .WithName("ScaleWorker");



        return app;
    }
}
