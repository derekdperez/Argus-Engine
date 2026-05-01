using Radzen;
using Amazon;
using Amazon.ECS;
using Amazon.Runtime;
using System.Net.Http;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.FileStore;
using NightmareV2.Application.HighValue;
using NightmareV2.Application.Workers;
using NightmareV2.CommandCenter;
using NightmareV2.CommandCenter.Components;
using NightmareV2.CommandCenter.DataMaintenance;
using NightmareV2.CommandCenter.Diagnostics;
using NightmareV2.CommandCenter.Endpoints;
using NightmareV2.CommandCenter.Hubs;
using NightmareV2.CommandCenter.Models;
using NightmareV2.CommandCenter.Realtime;
using NightmareV2.Contracts.Events;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using AssignPublicIp = Amazon.ECS.AssignPublicIp;
using AssetAdmissionStage = NightmareV2.Contracts.AssetAdmissionStage;
using AssetKind = NightmareV2.Contracts.AssetKind;
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
using UrlFetchSnapshot = NightmareV2.Application.Assets.UrlFetchSnapshot;

var builder = WebApplication.CreateBuilder(args);

OpsSnapshotBuilder.RegisterHttpClient(builder);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();

builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

builder.Services.AddNightmareInfrastructure(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddScoped<DiscoveryRealtimeClient>();
builder.Services.AddNightmareRabbitMq(
    builder.Configuration,
    consumers =>
    {
        consumers.AddConsumer<TargetCreatedUiEventConsumer>();
        consumers.AddConsumer<AssetDiscoveredUiEventConsumer>();
        consumers.AddConsumer<ScannableContentAvailableUiEventConsumer>();
        consumers.AddConsumer<CriticalHighValueFindingAlertUiEventConsumer>();
        consumers.AddConsumer<PortScanRequestedUiEventConsumer>();
        consumers.AddConsumer<SubdomainEnumerationRequestedUiEventConsumer>();
    });
builder.Services.AddOptions<NightmareRuntimeOptions>()
    .Bind(builder.Configuration.GetSection("Nightmare"))
    .Validate(
        o => !o.Diagnostics.Enabled || !string.IsNullOrWhiteSpace(o.Diagnostics.ApiKey),
        "Nightmare:Diagnostics:Enabled=true requires Nightmare:Diagnostics:ApiKey.")
    .Validate(
        o => !o.DataMaintenance.Enabled || !string.IsNullOrWhiteSpace(o.DataMaintenance.ApiKey),
        "Nightmare:DataMaintenance:Enabled=true requires Nightmare:DataMaintenance:ApiKey.")
    .ValidateOnStart();

var app = builder.Build();

var skipStartupDatabase = app.Configuration.GetValue("Nightmare:SkipStartupDatabase", false)
    || string.Equals(
        Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"),
        "1",
        StringComparison.OrdinalIgnoreCase);

await InitializeStartupDatabasesAsync(app, skipStartupDatabase).ConfigureAwait(false);

var listenPlainHttp = app.Configuration.GetValue("Nightmare:ListenPlainHttp", false);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    if (!listenPlainHttp)
        app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!listenPlainHttp)
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

DiagnosticsEndpoints.Map(app);
DataMaintenanceEndpoints.Map(app);
AdminUsageEndpoints.Map(app);
EventTraceEndpoints.Map(app);
AssetGraphEndpoints.Map(app);
TagEndpoints.Map(app);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<DiscoveryHub>("/hubs/discovery");

app.MapGet(
        "/api/targets",
        async (NightmareDbContext db, CancellationToken ct) =>
        {
            var targets = await db.Targets.AsNoTracking()
                .OrderByDescending(t => t.CreatedAtUtc)
                .Take(5000)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var targetIds = targets.Select(t => t.Id).ToList();
            var now = DateTimeOffset.UtcNow;
            var assetRollups = await db.Assets.AsNoTracking()
                .Where(a => targetIds.Contains(a.TargetId))
                .GroupBy(a => a.TargetId)
                .Select(
                    g => new
                    {
                        TargetId = g.Key,
                        Subdomains = g.LongCount(a => a.Kind == AssetKind.Subdomain),
                        Confirmed = g.LongCount(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed),
                        Queued = g.LongCount(a => a.LifecycleStatus == AssetLifecycleStatus.Queued),
                        LastAssetAtUtc = g.Max(a => (DateTimeOffset?)a.DiscoveredAtUtc),
                    })
                .ToDictionaryAsync(x => x.TargetId, ct)
                .ConfigureAwait(false);

            var queueRollups = await db.HttpRequestQueue.AsNoTracking()
                .Where(q => targetIds.Contains(q.TargetId))
                .GroupBy(q => q.TargetId)
                .Select(
                    g => new
                    {
                        TargetId = g.Key,
                        Queued = g.LongCount(q => q.State == HttpRequestQueueState.Queued
                            || (q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now)),
                        LastQueueAtUtc = g.Max(q => (DateTimeOffset?)q.UpdatedAtUtc),
                    })
                .ToDictionaryAsync(x => x.TargetId, ct)
                .ConfigureAwait(false);

            var rows = targets
                .Select(
                    t =>
                    {
                        assetRollups.TryGetValue(t.Id, out var assets);
                        queueRollups.TryGetValue(t.Id, out var queue);
                        var lastRun = MaxUtc(assets?.LastAssetAtUtc, queue?.LastQueueAtUtc);
                        return new TargetSummary(
                            t.Id,
                            t.RootDomain,
                            t.GlobalMaxDepth,
                            t.CreatedAtUtc,
                            assets?.Subdomains ?? 0,
                            assets?.Confirmed ?? 0,
                            queue?.Queued ?? assets?.Queued ?? 0,
                            lastRun);
                    })
                .ToList();

            return Results.Ok(rows);
        })
    .WithName("ListTargets");

static DateTimeOffset? MaxUtc(DateTimeOffset? first, DateTimeOffset? second)
{
    if (first is null)
        return second;
    if (second is null)
        return first;
    return first > second ? first : second;
}

static string[] RequiredWorkerKeys() =>
[
    WorkerKeys.Gatekeeper,
    WorkerKeys.Spider,
    WorkerKeys.Enumeration,
    WorkerKeys.PortScan,
    WorkerKeys.HighValueRegex,
    WorkerKeys.HighValuePaths,
    WorkerKeys.TechnologyIdentification,
];

static (string ScaleKey, string DefaultServiceName)? WorkerScaleTargetForKey(string workerKey) =>
    workerKey switch
    {
        WorkerKeys.Spider => ("worker-spider", "nightmare-worker-spider"),
        WorkerKeys.Enumeration => ("worker-enum", "nightmare-worker-enum"),
        WorkerKeys.PortScan => ("worker-portscan", "nightmare-worker-portscan"),
        WorkerKeys.HighValueRegex or WorkerKeys.HighValuePaths => ("worker-highvalue", "nightmare-worker-highvalue"),
        WorkerKeys.TechnologyIdentification => ("worker-techid", "nightmare-worker-techid"),
        _ => null,
    };

static (string ScaleKey, string DefaultServiceName, string DisplayName)[] WorkerScaleDefinitions() =>
[
    ("worker-spider", "nightmare-worker-spider", "Spider Worker"),
    ("worker-enum", "nightmare-worker-enum", "Subdomain Enum Worker"),
    ("worker-portscan", "nightmare-worker-portscan", "Port Scan Worker"),
    ("worker-highvalue", "nightmare-worker-highvalue", "High Value Worker"),
    ("worker-techid", "nightmare-worker-techid", "Technology Identification Worker"),
];

static WorkerScalingSettingsDto DefaultWorkerScalingSetting(string scaleKey)
{
    var displayName = WorkerScaleDefinitions().FirstOrDefault(d => d.ScaleKey == scaleKey).DisplayName ?? scaleKey;
    return scaleKey switch
    {
        "worker-spider" => new(scaleKey, displayName, 1, 50, 100, DateTimeOffset.UnixEpoch),
        "worker-enum" => new(scaleKey, displayName, 1, 20, 25, DateTimeOffset.UnixEpoch),
        "worker-portscan" => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
        "worker-highvalue" => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
        "worker-techid" => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
        _ => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
    };
}

static string EcsServiceNameForScaleKey(IConfiguration configuration, string scaleKey, string defaultServiceName)
{
    var envName = scaleKey switch
    {
        "worker-spider" => "WORKER_SPIDER_SERVICE",
        "worker-enum" => "WORKER_ENUM_SERVICE",
        "worker-portscan" => "WORKER_PORTSCAN_SERVICE",
        "worker-highvalue" => "WORKER_HIGHVALUE_SERVICE",
        "worker-techid" => "WORKER_TECHID_SERVICE",
        _ => "",
    };

    return string.IsNullOrWhiteSpace(envName)
        ? defaultServiceName
        : configuration[envName] ?? defaultServiceName;
}

static string EcsTaskFamilyForScaleKey(IConfiguration configuration, string scaleKey)
{
    var envName = scaleKey switch
    {
        "worker-spider" => "ECS_TASK_FAMILY_WORKER_SPIDER",
        "worker-enum" => "ECS_TASK_FAMILY_WORKER_ENUM",
        "worker-portscan" => "ECS_TASK_FAMILY_WORKER_PORTSCAN",
        "worker-highvalue" => "ECS_TASK_FAMILY_WORKER_HIGHVALUE",
        "worker-techid" => "ECS_TASK_FAMILY_WORKER_TECHID",
        _ => "",
    };

    return string.IsNullOrWhiteSpace(envName)
        ? $"nightmare-v2-{scaleKey}"
        : configuration[envName] ?? $"nightmare-v2-{scaleKey}";
}

static List<string> SplitCsvConfiguration(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? []
        : value
            .Replace(' ', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

static async Task<string?> ResolveAwsRegionAsync(IConfiguration configuration, CancellationToken ct)
{
    var configured = configuration["AWS_REGION"]
        ?? configuration["AWS_DEFAULT_REGION"]
        ?? configuration["AWS:Region"];
    if (!string.IsNullOrWhiteSpace(configured))
        return configured.Trim();

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var token = "";
        using (var tokenRequest = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token"))
        {
            tokenRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "60");
            using var tokenResponse = await http.SendAsync(tokenRequest, ct).ConfigureAwait(false);
            if (tokenResponse.IsSuccessStatusCode)
                token = await tokenResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        using var identityRequest = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/dynamic/instance-identity/document");
        if (!string.IsNullOrWhiteSpace(token))
            identityRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);

        using var identityResponse = await http.SendAsync(identityRequest, ct).ConfigureAwait(false);
        if (!identityResponse.IsSuccessStatusCode)
            return null;

        var json = await identityResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("region", out var region) ? region.GetString() : null;
    }
    catch
    {
        return null;
    }
}

static async Task<Dictionary<string, EcsService>> DescribeEcsServicesAsync(
    IConfiguration configuration,
    IEnumerable<string> serviceNames,
    CancellationToken ct)
{
    var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(region))
        return [];

    var cluster = configuration["ECS_CLUSTER"] ?? "nightmare-v2";
    using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
    var response = await ecs.DescribeServicesAsync(
            new DescribeServicesRequest
            {
                Cluster = cluster,
                Services = serviceNames.Distinct(StringComparer.Ordinal).ToList(),
            },
            ct)
        .ConfigureAwait(false);

    return response.Services.ToDictionary(s => s.ServiceName, StringComparer.Ordinal);
}



static async Task UpdateEcsServiceDesiredCountAsync(
    IConfiguration configuration,
    string serviceName,
    int desiredCount,
    CancellationToken ct)
{
    var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(region))
        throw new InvalidOperationException("AWS region is not configured and could not be inferred from EC2 metadata.");

    var cluster = configuration["ECS_CLUSTER"] ?? "nightmare-v2";
    using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
    await ecs.UpdateServiceAsync(
            new UpdateServiceRequest
            {
                Cluster = cluster,
                Service = serviceName,
                DesiredCount = desiredCount,
            },
            ct)
        .ConfigureAwait(false);
}

static async Task<(bool Changed, string Action)> EnsureEcsWorkerServiceDesiredCountAsync(
    IConfiguration configuration,
    string scaleKey,
    string serviceName,
    int desiredCount,
    CancellationToken ct)
{
    var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(region))
        throw new InvalidOperationException("AWS region is not configured and could not be inferred from EC2 metadata.");

    var cluster = configuration["ECS_CLUSTER"] ?? "nightmare-v2";
    using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
    var services = await ecs.DescribeServicesAsync(
            new DescribeServicesRequest
            {
                Cluster = cluster,
                Services = [serviceName],
            },
            ct)
        .ConfigureAwait(false);

    var service = services.Services.FirstOrDefault(s => string.Equals(s.ServiceName, serviceName, StringComparison.Ordinal));
    if (service is not null)
    {
        if (!string.Equals(service.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ECS service {serviceName} is {service.Status}; wait for it to become ACTIVE or finish deletion.");

        await ecs.UpdateServiceAsync(
                new UpdateServiceRequest
                {
                    Cluster = cluster,
                    Service = serviceName,
                    DesiredCount = desiredCount,
                },
                ct)
            .ConfigureAwait(false);

        return (true, "updated");
    }

    if (desiredCount == 0)
        return (false, "saved");

    var family = EcsTaskFamilyForScaleKey(configuration, scaleKey);
    var taskDefinitions = await ecs.ListTaskDefinitionsAsync(
            new ListTaskDefinitionsRequest
            {
                FamilyPrefix = family,
                Status = TaskDefinitionStatus.ACTIVE,
                Sort = SortOrder.DESC,
                MaxResults = 1,
            },
            ct)
        .ConfigureAwait(false);
    var taskDefinition = taskDefinitions.TaskDefinitionArns.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(taskDefinition))
        throw new InvalidOperationException($"No active ECS task definition was found for family {family}. Run deploy/aws/deploy-ecs-services.sh or ./deploy/deploy.sh --ecs-workers once to register it.");

    var subnets = SplitCsvConfiguration(configuration["ECS_SUBNETS"]);
    var securityGroups = SplitCsvConfiguration(configuration["ECS_SECURITY_GROUPS"]);
    if (subnets.Count == 0 || securityGroups.Count == 0)
        throw new InvalidOperationException("ECS_SUBNETS and ECS_SECURITY_GROUPS must be configured before Command Center can create worker services.");

    var request = new CreateServiceRequest
    {
        Cluster = cluster,
        ServiceName = serviceName,
        TaskDefinition = taskDefinition,
        DesiredCount = desiredCount,
        LaunchType = LaunchType.FindValue(configuration["ECS_LAUNCH_TYPE"] ?? "FARGATE"),
        DeploymentConfiguration = new DeploymentConfiguration
        {
            MinimumHealthyPercent = configuration.GetValue<int?>("ECS_MIN_HEALTHY_PERCENT") ?? 100,
            MaximumPercent = configuration.GetValue<int?>("ECS_MAX_PERCENT") ?? 200,
        },
        NetworkConfiguration = new NetworkConfiguration
        {
            AwsvpcConfiguration = new AwsVpcConfiguration
            {
                Subnets = subnets,
                SecurityGroups = securityGroups,
                AssignPublicIp = AssignPublicIp.FindValue(configuration["ECS_ASSIGN_PUBLIC_IP"] ?? "DISABLED"),
            },
        },
    };

    if (bool.TryParse(configuration["ECS_ENABLE_EXECUTE_COMMAND"], out var enableExecuteCommand) && enableExecuteCommand)
        request.EnableExecuteCommand = true;

    await ecs.CreateServiceAsync(request, ct).ConfigureAwait(false);
    return (true, "created");
}

static string ExtractTaskDefinitionVersion(string? taskDefinition)
{
    if (string.IsNullOrWhiteSpace(taskDefinition))
        return "-";

    var slash = taskDefinition.LastIndexOf("/", StringComparison.Ordinal);
    var familyRevision = slash >= 0 ? taskDefinition[(slash + 1)..] : taskDefinition;
    return familyRevision;
}

static async Task QueueRootSpiderSeedsAsync(
    IEventOutbox outbox,
    Guid targetId,
    string rootDomain,
    int globalMaxDepth,
    DateTimeOffset occurredAtUtc,
    Guid correlationId,
    Guid causationId,
    CancellationToken ct)
{
    foreach (var rootUrl in RootSpiderSeedUrls(rootDomain))
    {
        await outbox.EnqueueAsync(
                new AssetDiscovered(
                    targetId,
                    rootDomain,
                    globalMaxDepth,
                    0,
                    AssetKind.Url,
                    rootUrl,
                    "command-center-root-seed",
                    occurredAtUtc,
                    correlationId,
                    AssetAdmissionStage.Raw,
                    null,
                    "Target root domain spider seed",
                    EventId: NewId.NextGuid(),
                    CausationId: causationId == Guid.Empty ? correlationId : causationId,
                    Producer: "command-center"),
                ct)
            .ConfigureAwait(false);
    }
}

static IEnumerable<string> RootSpiderSeedUrls(string rootDomain)
{
    var host = rootDomain.Trim().Trim('/').TrimEnd('.');
    if (host.Length == 0)
        yield break;

    yield return $"https://{host}/";
    yield return $"http://{host}/";
}

app.MapPost(
        "/api/targets",
        async (
            CreateTargetRequest dto,
            NightmareDbContext db,
            IEventOutbox outbox,
            IHubContext<DiscoveryHub> hub,
            CancellationToken ct) =>
        {
            if (!TargetRootNormalization.TryNormalize(dto.RootDomain, out var root))
                return Results.BadRequest("root domain required");

            var target = new ReconTarget
            {
                Id = Guid.NewGuid(),
                RootDomain = root,
                GlobalMaxDepth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            db.Targets.Add(target);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var correlation = NewId.NextGuid();
            var eventId = NewId.NextGuid();
            await QueueRootSpiderSeedsAsync(outbox, target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc, correlation, eventId, ct)
                .ConfigureAwait(false);
            await outbox.EnqueueAsync(
                    new TargetCreated(
                        target.Id,
                        target.RootDomain,
                        target.GlobalMaxDepth,
                        target.CreatedAtUtc,
                        correlation,
                        EventId: eventId,
                        CausationId: correlation,
                        Producer: "command-center"),
                    ct)
                .ConfigureAwait(false);

            await hub.Clients.All.SendAsync(DiscoveryHubEvents.TargetQueued, target.Id, target.RootDomain, cancellationToken: ct)
                .ConfigureAwait(false);
            await hub.Clients.All.SendAsync(
                    DiscoveryHubEvents.DomainEvent,
                    new LiveUiEventDto(
                        "TargetCreated",
                        target.Id,
                        target.Id,
                        "targets",
                        $"Target queued: {target.RootDomain}",
                        target.CreatedAtUtc),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Created($"/api/targets/{target.Id}", new TargetSummary(target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc));
        })
    .WithName("CreateTarget");

app.MapPut(
        "/api/targets/{id:guid}",
        async (Guid id, UpdateTargetRequest dto, NightmareDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
        {
            if (!TargetRootNormalization.TryNormalize(dto.RootDomain, out var root))
                return Results.BadRequest("root domain required");

            var depth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12;
            var target = await db.Targets.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (target is null)
                return Results.NotFound();

            if (!string.Equals(target.RootDomain, root, StringComparison.Ordinal))
            {
                var taken = await db.Targets.AnyAsync(t => t.RootDomain == root && t.Id != id, ct).ConfigureAwait(false);
                if (taken)
                    return Results.Conflict("root domain already in use");
            }

            target.RootDomain = root;
            target.GlobalMaxDepth = depth;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var summary = new TargetSummary(target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc);
            await hub.Clients.All.SendAsync(
                    DiscoveryHubEvents.DomainEvent,
                    new LiveUiEventDto(
                        "TargetUpdated",
                        target.Id,
                        target.Id,
                        "targets",
                        $"Target updated: {target.RootDomain}",
                        DateTimeOffset.UtcNow),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            return Results.Ok(summary);
        })
    .WithName("UpdateTarget");

app.MapPut(
        "/api/targets/max-depth",
        async (UpdateTargetMaxDepthRequest dto, NightmareDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
        {
            if (dto.GlobalMaxDepth <= 0)
                return Results.BadRequest("globalMaxDepth must be greater than zero");

            IQueryable<ReconTarget> query = db.Targets;
            if (!dto.AllTargets)
            {
                if (dto.TargetIds is null || dto.TargetIds.Count == 0)
                    return Results.BadRequest("targetIds is required unless allTargets is true");

                var ids = dto.TargetIds.Distinct().ToArray();
                query = query.Where(t => ids.Contains(t.Id));
            }

            var updated = await query
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.GlobalMaxDepth, dto.GlobalMaxDepth),
                    ct)
                .ConfigureAwait(false);

            await hub.Clients.All.SendAsync(
                    DiscoveryHubEvents.DomainEvent,
                    new LiveUiEventDto(
                        "TargetsMaxDepthUpdated",
                        null,
                        null,
                        "targets",
                        dto.AllTargets
                            ? $"Max depth set to {dto.GlobalMaxDepth} for all targets"
                            : $"Max depth set to {dto.GlobalMaxDepth} for {updated} targets",
                        DateTimeOffset.UtcNow),
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Ok(new UpdateTargetMaxDepthResult(updated, dto.GlobalMaxDepth));
        })
    .WithName("UpdateTargetsMaxDepth");

app.MapDelete(
        "/api/targets/{id:guid}",
        async (Guid id, NightmareDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
        {
            var target = await db.Targets.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (target is null)
                return Results.NotFound();
            var rootDomain = target.RootDomain;
            db.Targets.Remove(target);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await hub.Clients.All.SendAsync(
                    DiscoveryHubEvents.DomainEvent,
                    new LiveUiEventDto(
                        "TargetDeleted",
                        id,
                        id,
                        "targets",
                        $"Target deleted: {rootDomain}",
                        DateTimeOffset.UtcNow),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            return Results.NoContent();
        })
    .WithName("DeleteTarget");

app.MapPost(
        "/api/targets/bulk",
        async (HttpRequest httpRequest, NightmareDbContext db, IEventOutbox outbox, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
        {
            const int maxLines = 50_000;
            var rawLines = new List<string>();
            var globalDepth = 12;
            var contentType = httpRequest.ContentType ?? "";

            if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var form = await httpRequest.ReadFormAsync(ct).ConfigureAwait(false);
                if (form.TryGetValue("globalMaxDepth", out var depthVals) && int.TryParse(depthVals.ToString(), out var parsedDepth) && parsedDepth > 0)
                    globalDepth = parsedDepth;
                var file = form.Files.GetFile("file");
                if (file is null || file.Length == 0)
                    return Results.BadRequest("multipart field \"file\" is required");
                await using var stream = file.OpenReadStream();
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                rawLines.AddRange(TargetRootNormalization.SplitLines(text));
            }
            else
            {
                var dto = await httpRequest.ReadFromJsonAsync<BulkImportRequest>(cancellationToken: ct).ConfigureAwait(false);
                if (dto is null)
                    return Results.BadRequest("expected JSON body or multipart/form-data with field \"file\"");
                globalDepth = dto.GlobalMaxDepth > 0 ? dto.GlobalMaxDepth : 12;
                if (dto.Domains is not null)
                    rawLines.AddRange(dto.Domains);
            }

            if (rawLines.Count > maxLines)
                return Results.BadRequest($"maximum {maxLines} lines per import");

            var firstOrder = new List<string>();
            var batchSeen = new HashSet<string>(StringComparer.Ordinal);
            var skippedEmpty = 0;
            var skippedDupBatch = 0;
            var skippedInvalid = 0;
            foreach (var line in rawLines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    skippedEmpty++;
                    continue;
                }

                if (!TargetRootNormalization.TryNormalize(trimmed, out var n))
                {
                    skippedInvalid++;
                    continue;
                }

                if (!batchSeen.Add(n))
                {
                    skippedDupBatch++;
                    continue;
                }

                firstOrder.Add(n);
            }

            if (firstOrder.Count == 0)
            {
                return Results.Ok(
                    new BulkImportResult(
                        0,
                        0,
                        skippedInvalid + skippedEmpty,
                        skippedDupBatch));
            }

            var existing = await db.Targets.AsNoTracking()
                .Where(t => firstOrder.Contains(t.RootDomain))
                .Select(t => t.RootDomain)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);

            var skippedExist = 0;
            var newTargets = new List<ReconTarget>();
            foreach (var n in firstOrder)
            {
                if (existingSet.Contains(n))
                {
                    skippedExist++;
                    continue;
                }

                existingSet.Add(n);
                var target = new ReconTarget
                {
                    Id = Guid.NewGuid(),
                    RootDomain = n,
                    GlobalMaxDepth = globalDepth,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                newTargets.Add(target);
                db.Targets.Add(target);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var target in newTargets)
            {
                var correlation = NewId.NextGuid();
                var eventId = NewId.NextGuid();
                await QueueRootSpiderSeedsAsync(outbox, target.Id, target.RootDomain, target.GlobalMaxDepth, target.CreatedAtUtc, correlation, eventId, ct)
                    .ConfigureAwait(false);
                await outbox.EnqueueAsync(
                        new TargetCreated(
                            target.Id,
                            target.RootDomain,
                            target.GlobalMaxDepth,
                            target.CreatedAtUtc,
                            correlation,
                            EventId: eventId,
                            CausationId: correlation,
                            Producer: "command-center"),
                        ct)
                    .ConfigureAwait(false);
                await hub.Clients.All.SendAsync(DiscoveryHubEvents.TargetQueued, target.Id, target.RootDomain, cancellationToken: ct)
                    .ConfigureAwait(false);
                await hub.Clients.All.SendAsync(
                        DiscoveryHubEvents.DomainEvent,
                        new LiveUiEventDto(
                            "TargetCreated",
                            target.Id,
                            target.Id,
                            "targets",
                            $"Target queued: {target.RootDomain}",
                            target.CreatedAtUtc),
                        cancellationToken: ct)
                    .ConfigureAwait(false);
            }

            return Results.Ok(
                new BulkImportResult(
                    newTargets.Count,
                    skippedExist,
                    skippedInvalid + skippedEmpty,
                    skippedDupBatch));
        })
    .WithName("BulkImportTargets");


app.MapGet(
        "/api/http-request-queue/settings",
        async (NightmareDbContext db, CancellationToken ct) =>
        {
            var row = await db.HttpRequestQueueSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct)
                .ConfigureAwait(false)
                ?? new HttpRequestQueueSettings();

            return Results.Ok(
                new HttpRequestQueueSettingsDto(
                    row.Enabled,
                    row.GlobalRequestsPerMinute,
                    row.PerDomainRequestsPerMinute,
                    row.MaxConcurrency,
                    row.RequestTimeoutSeconds,
                    row.UpdatedAtUtc));
        })
    .WithName("GetHttpRequestQueueSettings");

app.MapPut(
        "/api/http-request-queue/settings",
        async (HttpRequestQueueSettingsPatch body, NightmareDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
        {
            var row = await db.HttpRequestQueueSettings.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new HttpRequestQueueSettings { Id = 1 };
                db.HttpRequestQueueSettings.Add(row);
            }

            row.Enabled = body.Enabled;
            row.GlobalRequestsPerMinute = Math.Clamp(body.GlobalRequestsPerMinute, 1, 100_000);
            row.PerDomainRequestsPerMinute = Math.Clamp(body.PerDomainRequestsPerMinute, 1, 10_000);
            row.MaxConcurrency = Math.Clamp(body.MaxConcurrency, 1, 1_000);
            row.RequestTimeoutSeconds = Math.Clamp(body.RequestTimeoutSeconds, 5, 300);
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await hub.Clients.All.SendAsync(
                    DiscoveryHubEvents.DomainEvent,
                    new LiveUiEventDto(
                        "QueueSettingsChanged",
                        null,
                        null,
                        "http",
                        body.Enabled ? "HTTP queue enabled" : "HTTP queue disabled",
                        row.UpdatedAtUtc),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            return Results.NoContent();
        })
    .WithName("UpdateHttpRequestQueueSettings");

app.MapGet(
        "/api/http-request-queue",
        async (NightmareDbContext db, Guid? targetId, string? state, int? take, CancellationToken ct) =>
        {
            var q = db.HttpRequestQueue.AsNoTracking().AsQueryable();
            if (targetId is { } tid)
                q = q.Where(r => r.TargetId == tid);

            if (!string.IsNullOrWhiteSpace(state))
            {
                var requestedState = state.Trim();
                q = q.Where(r => r.State == requestedState);
            }

            IQueryable<HttpRequestQueueItem> ordered = q.OrderByDescending(r => r.CreatedAtUtc);
            if (take is > 0)
                ordered = ordered.Take(Math.Clamp(take.Value, 1, 100_000));

            var rows = await ordered
                .Select(r => new HttpRequestQueueRowDto(
                    r.Id,
                    r.AssetId,
                    r.TargetId,
                    r.AssetKind.ToString(),
                    r.Method,
                    r.RequestUrl,
                    r.DomainKey,
                    r.State.ToString(),
                    r.AttemptCount,
                    r.MaxAttempts,
                    r.Priority,
                    r.CreatedAtUtc,
                    r.UpdatedAtUtc,
                    r.NextAttemptAtUtc,
                    r.StartedAtUtc,
                    r.CompletedAtUtc,
                    r.LockedBy,
                    r.LockedUntilUtc,
                    r.LastHttpStatus,
                    r.LastError,
                    r.RequestHeadersJson,
                    r.RequestBody,
                    r.ResponseHeadersJson,
                    r.ResponseBody,
                    r.DurationMs,
                    r.ResponseContentType,
                    r.ResponseContentLength,
                    r.FinalUrl,
                    r.RedirectCount))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(rows);
        })
    .WithName("ListHttpRequestQueue");

app.MapGet(
        "/api/http-request-queue/metrics",
        async (NightmareDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var oneMinuteAgo = now.AddMinutes(-1);
            var oneHourAgo = now.AddHours(-1);
            var oneDayAgo = now.AddHours(-24);

            var queued = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Queued, ct)
                .ConfigureAwait(false);
            var retry = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc <= now, ct)
                .ConfigureAwait(false);
            var scheduledRetry = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Retry && q.NextAttemptAtUtc > now, ct)
                .ConfigureAwait(false);
            var inFlight = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.InFlight, ct)
                .ConfigureAwait(false);
            var failed = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Failed, ct)
                .ConfigureAwait(false);
            var completedLastHour = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Succeeded && q.CompletedAtUtc >= oneHourAgo, ct)
                .ConfigureAwait(false);
            var failedLastMinute = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= oneMinuteAgo, ct)
                .ConfigureAwait(false);
            var failedLastHour = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= oneHourAgo, ct)
                .ConfigureAwait(false);
            var failedLast24Hours = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.State == HttpRequestQueueState.Failed && q.UpdatedAtUtc >= oneDayAgo, ct)
                .ConfigureAwait(false);
            var sentLastMinute = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.StartedAtUtc >= oneMinuteAgo, ct)
                .ConfigureAwait(false);
            var sentLastHour = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.StartedAtUtc >= oneHourAgo, ct)
                .ConfigureAwait(false);
            var sentLast24Hours = await db.HttpRequestQueue.AsNoTracking()
                .LongCountAsync(q => q.StartedAtUtc >= oneDayAgo, ct)
                .ConfigureAwait(false);
            var oldestQueuedAt = await db.HttpRequestQueue.AsNoTracking()
                .Where(q => q.State == HttpRequestQueueState.Queued)
                .OrderBy(q => q.CreatedAtUtc)
                .Select(q => (DateTimeOffset?)q.CreatedAtUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(
                new HttpRequestQueueMetricsDto(
                    queued,
                    retry,
                    scheduledRetry,
                    inFlight,
                    failed,
                    completedLastHour,
                    queued,
                    oldestQueuedAt,
                    oldestQueuedAt is null ? null : (long)(now - oldestQueuedAt.Value).TotalSeconds,
                    failedLastMinute,
                    failedLastHour,
                    failedLast24Hours,
                    sentLastMinute,
                    sentLastHour,
                    sentLast24Hours));
        })
    .WithName("GetHttpRequestQueueMetrics");

app.MapGet(
        "/api/bus/live",
        async (NightmareDbContext db, int? minutes, int? take, CancellationToken ct) =>
        {
            var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 3, 1, 60));
            var limit = Math.Clamp(take ?? 150, 1, 500);
            var since = DateTimeOffset.UtcNow - window;
            var rows = await db.BusJournal.AsNoTracking()
                .Where(e => e.Direction == "Publish" && e.OccurredAtUtc >= since)
                .OrderByDescending(e => e.OccurredAtUtc)
                .Take(limit)
                .Select(e => new BusJournalRowDto(e.Id, e.Direction, e.MessageType, e.PayloadJson, e.OccurredAtUtc, e.ConsumerType, e.HostName))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(rows);
        })
    .WithName("BusLive");

app.MapGet(
        "/api/bus/history",
        async (NightmareDbContext db, int? take, CancellationToken ct) =>
        {
            var limit = Math.Clamp(take ?? 400, 1, 2000);
            var rows = await db.BusJournal.AsNoTracking()
                .OrderByDescending(e => e.Id)
                .Take(limit)
                .Select(e => new BusJournalRowDto(e.Id, e.Direction, e.MessageType, e.PayloadJson, e.OccurredAtUtc, e.ConsumerType, e.HostName))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(rows);
        })
    .WithName("BusHistory");

app.MapGet(
        "/api/assets",
        async (NightmareDbContext db, Guid? targetId, int? take, string? tag, CancellationToken ct) =>
        {
            var q = db.Assets.AsNoTracking()
                .Where(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
                .OrderByDescending(a => a.DiscoveredAtUtc)
                .AsQueryable();
            if (targetId is { } tid)
                q = q.Where(a => a.TargetId == tid);
            if (!string.IsNullOrWhiteSpace(tag))
            {
                var tagSlug = tag.Trim();
                q = q.Where(a => db.AssetTags.Any(at => at.AssetId == a.Id && db.Tags.Any(t => t.Id == at.TagId && t.Slug == tagSlug)));
            }

            if (take is > 0)
                q = q.Take(Math.Clamp(take.Value, 1, 1_000_000));

            var rows = await q
                .Select(a => new AssetGridRowDto(
                    a.Id,
                    a.TargetId,
                    a.Kind.ToString(),
                    a.Category.ToString(),
                    a.CanonicalKey,
                    a.RawValue,
                    a.DisplayName,
                    a.Depth,
                    a.DiscoveredBy,
                    a.DiscoveryContext,
                    a.DiscoveredAtUtc,
                    a.LastSeenAtUtc,
                    a.Confidence,
                    a.LifecycleStatus,
                    a.TypeDetailsJson,
                    a.FinalUrl,
                    a.RedirectCount))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(rows);
        })
    .WithName("ListAssets");

const long fileStoreMaxUploadBytes = 50L * 1024 * 1024;

app.MapPost(
        "/api/filestore",
        async (HttpRequest req, IFileStore store, CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest("multipart/form-data with field \"file\" is required");
            var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest("multipart field \"file\" is required");
            if (file.Length > fileStoreMaxUploadBytes)
                return Results.BadRequest($"file exceeds maximum size ({fileStoreMaxUploadBytes} bytes)");
            var logical = form["logicalName"].ToString();
            if (string.IsNullOrWhiteSpace(logical))
                logical = file.FileName;
            await using var uploadStream = file.OpenReadStream();
            var created = await store.StoreAsync(uploadStream, file.ContentType, logical, ct).ConfigureAwait(false);
            return Results.Created($"/api/filestore/{created.Id}", created);
        })
    .WithName("UploadFileBlob")
    .DisableAntiforgery();

app.MapGet(
        "/api/filestore/{id:guid}",
        async (Guid id, IFileStore store, CancellationToken ct) =>
        {
            var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
            return meta is null ? Results.NotFound() : Results.Ok(meta);
        })
    .WithName("GetFileBlobInfo");

app.MapGet(
        "/api/filestore/{id:guid}/download",
        async (Guid id, IFileStore store, CancellationToken ct) =>
        {
            var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
            if (meta is null)
                return Results.NotFound();
            var stream = await store.OpenReadAsync(id, ct).ConfigureAwait(false);
            if (stream is null)
                return Results.NotFound();
            return Results.File(
                stream,
                meta.ContentType ?? "application/octet-stream",
                fileDownloadName: meta.LogicalName ?? $"{meta.Id:N}");
        })
    .WithName("DownloadFileBlob");

app.MapDelete(
        "/api/filestore/{id:guid}",
        async (Guid id, IFileStore store, CancellationToken ct) =>
        {
            var meta = await store.GetDescriptorAsync(id, ct).ConfigureAwait(false);
            if (meta is null)
                return Results.NotFound();
            await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
    .WithName("DeleteFileBlob");

app.MapGet(
        "/api/high-value-findings",
        async (NightmareDbContext db, bool? criticalOnly, int? take, CancellationToken ct) =>
        {
            var q =
                from f in db.HighValueFindings.AsNoTracking()
                join t in db.Targets.AsNoTracking() on f.TargetId equals t.Id
                join a in db.Assets.AsNoTracking() on f.SourceAssetId equals (Guid?)a.Id into assetJoin
                from a in assetJoin.DefaultIfEmpty()
                select new
                {
                    f,
                    t.RootDomain,
                    AssetLifecycleStatus = a == null ? null : a.LifecycleStatus,
                    TypeDetailsJson = a == null ? null : a.TypeDetailsJson,
                };

            if (criticalOnly == true)
                q = q.Where(x => x.f.Severity == "Critical");

            var ordered = q.OrderByDescending(x => x.f.DiscoveredAtUtc);
            var rowQuery = take is > 0
                ? ordered.Take(Math.Clamp(take.Value, 1, 1_000_000))
                : ordered;

            var rows = await rowQuery
                .Select(x => new HighValueFindingRowDto(
                    x.f.Id,
                    x.f.TargetId,
                    x.f.SourceAssetId,
                    x.f.FindingType,
                    x.f.Severity,
                    x.f.PatternName,
                    x.f.Category ?? "",
                    x.f.MatchedText ?? "",
                    x.f.SourceUrl,
                    x.f.WorkerName,
                    x.f.ImportanceScore,
                    x.f.DiscoveredAtUtc,
                    x.RootDomain))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(rows);
        })
    .WithName("ListHighValueFindings");

static bool FindingSourceIsAllowed(
    HighValueFindingRowDto row,
    string discoveredBy,
    UrlFetchSnapshot snap,
    IReadOnlySet<string> allowedHighValuePaths)
{
    // Regex findings can be raised from any confirmed URL response. The previous page query
    // incorrectly limited every high-value finding to hvpath:* assets, which hid confirmed
    // assets that matched the high-value regex scanner.
    if (!discoveredBy.StartsWith("hvpath:", StringComparison.OrdinalIgnoreCase))
        return true;

    return HighValuePathRedirectIsAllowed(row, snap, allowedHighValuePaths);
}

static bool HighValuePathRedirectIsAllowed(
    HighValueFindingRowDto row,
    UrlFetchSnapshot snap,
    IReadOnlySet<string> allowedHighValuePaths)
{
    var source = NormalizeUrlForCompare(row.SourceUrl);
    if (source is null)
        return false;

    // Older confirmed snapshots may not include FinalUrl. Treat them as displayable after the
    // confirmed-status and soft-404 checks above; otherwise historical high-value assets vanish.
    var final = NormalizeUrlForCompare(snap.FinalUrl);
    if (final is null)
        return true;

    var redirected = !string.Equals(source, final, StringComparison.OrdinalIgnoreCase);
    if (!redirected)
        return true;
    if (!Uri.TryCreate(final, UriKind.Absolute, out var finalUri))
        return false;
    return allowedHighValuePaths.Contains(NormalizeWordlistPath(finalUri.AbsolutePath));
}

static string? NormalizeUrlForCompare(string? url)
{
    if (string.IsNullOrWhiteSpace(url))
        return null;
    if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
        return null;
    if (u.Scheme is not ("http" or "https"))
        return null;
    var canonical = u.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    return canonical.TrimEnd('/');
}

static IReadOnlySet<string> LoadHighValuePathSet()
{
    var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Wordlists", "high_value");
    var list = HighValueWordlistCatalog.LoadFromDirectory(dir);
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (_, lines) in list)
    {
        foreach (var line in lines)
            set.Add(NormalizeWordlistPath(line));
    }

    return set;
}

static string NormalizeWordlistPath(string path)
{
    var p = path.Trim();
    if (p.Length == 0)
        return "/";
    var q = p.IndexOfAny(['?', '#']);
    if (q >= 0)
        p = p[..q];
    if (!p.StartsWith('/'))
        p = "/" + p;
    return p.TrimEnd('/');
}

static bool TryParseSnapshot(string? typeDetailsJson, out UrlFetchSnapshot snapshot)
{
    snapshot = default!;
    if (string.IsNullOrWhiteSpace(typeDetailsJson))
        return false;
    try
    {
        snapshot = JsonSerializer.Deserialize<UrlFetchSnapshot>(typeDetailsJson)!;
        return snapshot is not null;
    }
    catch
    {
        return false;
    }
}

static bool LooksLikeSoft404(UrlFetchSnapshot? snap)
{
    if (snap is null)
        return true;
    if (snap.StatusCode is 404 or 410)
        return true;
    if (snap.StatusCode < 200 || snap.StatusCode >= 300)
        return true;

    var body = snap.ResponseBody;
    if (string.IsNullOrWhiteSpace(body))
        return false;

    var contentType = snap.ContentType ?? "";
    var textLike = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    if (!textLike)
        return false;

    var normalized = body.ToLowerInvariant();
    return normalized.Contains("404 not found", StringComparison.Ordinal)
        || normalized.Contains("page not found", StringComparison.Ordinal)
        || normalized.Contains("doesn't exist", StringComparison.Ordinal)
        || normalized.Contains("cannot be found", StringComparison.Ordinal)
        || normalized.Contains("the page you are looking for", StringComparison.Ordinal);
}

app.MapPost(
        "/api/ops/subdomain-enum/restart",
        async (RestartToolRequest body, NightmareDbContext db, IEventOutbox outbox, IOptions<SubdomainEnumerationOptions> options, CancellationToken ct) =>
        {
            var targetsQuery = db.Targets.AsNoTracking();
            if (!body.AllTargets)
            {
                if (body.TargetIds is null || body.TargetIds.Length == 0)
                    return Results.BadRequest("targetIds is required unless allTargets is true");

                var ids = body.TargetIds
                    .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
                    .Where(x => x != Guid.Empty)
                    .ToHashSet();
                if (ids.Count == 0)
                    return Results.BadRequest("no valid target ids supplied");

                targetsQuery = targetsQuery.Where(t => ids.Contains(t.Id));
            }

            var targets = await targetsQuery.Take(5000).ToListAsync(ct).ConfigureAwait(false);
            var providers = options.Value.DefaultProviders
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (providers.Length == 0)
                providers = ["subfinder", "amass"];

            var queued = 0;
            foreach (var target in targets)
            {
                var correlation = NewId.NextGuid();
                foreach (var provider in providers)
                {
                    var eventId = NewId.NextGuid();
                    await outbox.EnqueueAsync(
                            new SubdomainEnumerationRequested(
                                target.Id,
                                target.RootDomain,
                                provider,
                                "command-center-manual-restart",
                                DateTimeOffset.UtcNow,
                                correlation,
                                EventId: eventId,
                                CausationId: correlation,
                                Producer: "command-center"),
                            ct)
                        .ConfigureAwait(false);
                    queued++;
                }
            }

            return Results.Ok(new { Targets = targets.Count, JobsQueued = queued });
        })
    .WithName("RestartSubdomainEnumeration");

app.MapPost(
        "/api/ops/spider/restart",
        async (RestartToolRequest body, NightmareDbContext db, IEventOutbox outbox, CancellationToken ct) =>
        {
            var targetsQuery = db.Targets.AsNoTracking();
            if (!body.AllTargets)
            {
                if (body.TargetIds is null || body.TargetIds.Length == 0)
                    return Results.BadRequest("targetIds is required unless allTargets is true");

                var ids = body.TargetIds
                    .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
                    .Where(x => x != Guid.Empty)
                    .ToHashSet();
                if (ids.Count == 0)
                    return Results.BadRequest("no valid target ids supplied");

                targetsQuery = targetsQuery.Where(t => ids.Contains(t.Id));
            }

            var targets = await targetsQuery.Take(5000).ToListAsync(ct).ConfigureAwait(false);
            var targetIds = targets.Select(t => t.Id).ToHashSet();
            var now = DateTimeOffset.UtcNow;

            var existingQueueRows = await db.HttpRequestQueue
                .Where(q => targetIds.Contains(q.TargetId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var row in existingQueueRows)
            {
                row.State = HttpRequestQueueState.Queued;
                row.LockedBy = null;
                row.LockedUntilUtc = null;
                row.StartedAtUtc = null;
                row.CompletedAtUtc = null;
                row.LastError = null;
                row.UpdatedAtUtc = now;
                row.NextAttemptAtUtc = now;
            }

            var queuedRootSeeds = 0;
            foreach (var target in targets)
            {
                var correlation = NewId.NextGuid();
                await QueueRootSpiderSeedsAsync(outbox, target.Id, target.RootDomain, target.GlobalMaxDepth, now, correlation, correlation, ct)
                    .ConfigureAwait(false);
                queuedRootSeeds += 2;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { Targets = targets.Count, RequeuedExistingRequests = existingQueueRows.Count, RootSeedsQueued = queuedRootSeeds });
        })
    .WithName("RestartSpidering");

app.MapGet(
        "/api/workers",
        async (NightmareDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var persisted = await db.WorkerSwitches.AsNoTracking()
                .Select(w => new WorkerSwitchDto(w.WorkerKey, w.IsEnabled, w.UpdatedAtUtc))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var rows = RequiredWorkerKeys()
                .Select(key => persisted.FirstOrDefault(w => w.WorkerKey == key) ?? new WorkerSwitchDto(key, true, now))
                .Concat(persisted.Where(w => !RequiredWorkerKeys().Contains(w.WorkerKey, StringComparer.Ordinal)))
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
                        Last24h = g.LongCount(),
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
                    Last24h = spiderRows.LongCount(),
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
        async (NightmareDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            var overrides = await db.WorkerScaleTargets.AsNoTracking()
                .ToDictionaryAsync(t => t.ScaleKey, t => t, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            var definitions = RequiredWorkerKeys()
                .Select(key => new { WorkerKey = key, Target = WorkerScaleTargetForKey(key) })
                .Where(x => x.Target is not null)
                .Select(
                    x =>
                    {
                        var target = x.Target!.Value;
                        return new
                        {
                            x.WorkerKey,
                            target.ScaleKey,
                            ServiceName = EcsServiceNameForScaleKey(configuration, target.ScaleKey, target.DefaultServiceName),
                        };
                    })
                .ToList();

            Dictionary<string, EcsService> services = [];
            var ecsConfigured = false;
            var status = "ECS region is not configured and could not be inferred from EC2 metadata.";
            var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(region))
            {
                try
                {
                    services = await DescribeEcsServicesAsync(configuration, definitions.Select(d => d.ServiceName), ct).ConfigureAwait(false);
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

                    var fallback = DefaultWorkerScalingSetting(definition.ScaleKey);
                    var currentDesired = Math.Max(0, service.DesiredCount.GetValueOrDefault());
                    if (currentDesired >= fallback.MinTasks)
                        continue;

                    try
                    {
                        await UpdateEcsServiceDesiredCountAsync(configuration, definition.ServiceName, fallback.MinTasks, ct)
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
                        var fallback = DefaultWorkerScalingSetting(definition.ScaleKey);
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
        async (NightmareDbContext db, CancellationToken ct) =>
        {
            var persisted = await db.WorkerScalingSettings.AsNoTracking()
                .ToDictionaryAsync(s => s.ScaleKey, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            var rows = WorkerScaleDefinitions()
                .Select(
                    definition =>
                    {
                        var fallback = DefaultWorkerScalingSetting(definition.ScaleKey);
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
        async (string scaleKey, WorkerScalingSettingsPatchDto body, NightmareDbContext db, IHubContext<DiscoveryHub> hub, CancellationToken ct) =>
        {
            if (!WorkerScaleDefinitions().Any(d => string.Equals(d.ScaleKey, scaleKey, StringComparison.Ordinal)))
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

            var displayName = WorkerScaleDefinitions().First(d => d.ScaleKey == scaleKey).DisplayName;
            return Results.Ok(new WorkerScalingSettingsDto(scaleKey, displayName, row.MinTasks, row.MaxTasks, row.TargetBacklogPerTask, now));
        })
    .WithName("UpdateWorkerScalingSettings");

app.MapGet(
        "/api/ops/ecs-status",
        async (IConfiguration configuration, CancellationToken ct) =>
        {
            var at = DateTimeOffset.UtcNow;
            var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
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

            var definitions = WorkerScaleDefinitions()
                .Select(d => new
                {
                    d.ScaleKey,
                    d.DisplayName,
                    ServiceName = EcsServiceNameForScaleKey(configuration, d.ScaleKey, d.DefaultServiceName),
                })
                .ToList();

            try
            {
                var services = await DescribeEcsServicesAsync(configuration, definitions.Select(d => d.ServiceName), ct).ConfigureAwait(false);
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
                                ExtractTaskDefinitionVersion(taskDefinition),
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
            IHubContext<DiscoveryHub> hub,
            CancellationToken ct) =>
        {
            var target = WorkerScaleTargetForKey(key);
            if (target is null)
                return Results.BadRequest($"{key} does not map to a scalable ECS worker service.");

            var maxDesiredCount = configuration.GetValue<int?>("Nightmare:WorkerScaling:MaxDesiredCount") ?? 100;
            if (body.DesiredCount < 0 || body.DesiredCount > maxDesiredCount)
                return Results.BadRequest($"desiredCount must be between 0 and {maxDesiredCount}.");

            var scaleKey = target.Value.ScaleKey;
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

            var serviceName = EcsServiceNameForScaleKey(configuration, scaleKey, target.Value.DefaultServiceName);
            var ecsUpdated = false;
            var message = "Manual worker count saved. ECS was not updated because AWS region is not configured and could not be inferred from EC2 metadata.";
            var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(region))
            {
                try
                {
                    var result = await EnsureEcsWorkerServiceDesiredCountAsync(configuration, scaleKey, serviceName, body.DesiredCount, ct)
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


static async Task InitializeStartupDatabasesAsync(WebApplication app, bool skipStartupDatabase)
{
    var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    if (skipStartupDatabase)
    {
        startupLog.LogWarning(
            "Startup database EnsureCreated skipped (Nightmare:SkipStartupDatabase or NIGHTMARE_SKIP_STARTUP_DATABASE=1). "
            + "APIs that need Postgres will still fail until a database is reachable.");
        return;
    }

    var continueOnFailure = app.Configuration.GetValue("Nightmare:ContinueOnStartupDatabaseFailure", true);
    var retryDelays = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
    };

    for (var attempt = 1; attempt <= retryDelays.Length + 1; attempt++)
    {
        try
        {
            await StartupDatabaseBootstrap.InitializeAsync(
                    app.Services,
                    app.Configuration,
                    startupLog,
                    includeFileStore: true,
                    app.Lifetime.ApplicationStopping)
                .ConfigureAwait(false);
            startupLog.LogInformation("Startup database initialization completed.");
            return;
        }
        catch (Exception ex) when (attempt <= retryDelays.Length && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            startupLog.LogWarning(
                ex,
                "Startup database initialization failed on attempt {Attempt}; retrying.",
                attempt);
            await Task.Delay(retryDelays[attempt - 1], app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (!continueOnFailure)
                throw;

            startupLog.LogError(
                ex,
                "Startup database initialization failed after retries. Command Center will continue to serve /health and diagnostics, but database-backed APIs will fail until Postgres/schema is fixed.");
            return;
        }
    }
}

app.Run();
