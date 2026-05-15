using System.Text.Json;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Workers.Orchestration.Configuration;
using ArgusEngine.Workers.Orchestration.Persistence;
using ArgusEngine.Workers.Orchestration.State;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Workers.Orchestration.Services;

public sealed class ReconOrchestratorHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly IBus _bus;
    private readonly IOptionsMonitor<ReconOrchestratorOptions> _options;
    private readonly IReconOrchestratorRepository _repository;
    private readonly IReconProfilePlanner _profilePlanner;
    private readonly ILogger<ReconOrchestratorHostedService> _logger;

    public ReconOrchestratorHostedService(
        IBus bus,
        IOptionsMonitor<ReconOrchestratorOptions> options,
        IReconOrchestratorRepository repository,
        IReconProfilePlanner profilePlanner,
        ILogger<ReconOrchestratorHostedService> logger)
    {
        _bus = bus;
        _options = options;
        _repository = repository;
        _profilePlanner = profilePlanner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("ReconOrchestrator is disabled.");
            return;
        }

        if (options.ApplySchemaOnStartup)
        {
            await _repository.EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("ReconOrchestrator instance {InstanceId} started.", _instanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReconOrchestrator tick failed.");
            }

            await Task.Delay(_options.CurrentValue.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var targets = options.TargetIds.Count > 0
            ? await _repository.ResolveTargetsAsync(options.TargetIds, cancellationToken).ConfigureAwait(false)
            : await _repository.ListTargetsAsync(options.MaxTargetsPerTick, cancellationToken).ConfigureAwait(false);

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var acquired = await _repository.TryAcquireLeaseAsync(
                target.Id,
                _instanceId,
                options.LeaseTtl,
                cancellationToken).ConfigureAwait(false);

            if (!acquired)
            {
                continue;
            }

            await ReconcileTargetAsync(target, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileTargetAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorOptions options,
        CancellationToken cancellationToken)
    {
        var serializedConfiguration = JsonSerializer.Serialize(options, JsonOptions);
        var state = await _repository.LoadOrCreateStateAsync(
            target,
            options,
            serializedConfiguration,
            cancellationToken).ConfigureAwait(false);

        await EnsureEnumerationAsync(target, options, state, cancellationToken).ConfigureAwait(false);

        var allRequiredProvidersCompleted = options.EnumerationProviders
            .All(provider =>
                state.ProviderRuns.TryGetValue(provider, out var run)
                && string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase));

        if (options.RequireEnumerationBeforeSpidering && !allRequiredProvidersCompleted)
        {
            await _repository.SaveStateAsync(target.Id, _instanceId, state, serializedConfiguration, cancellationToken).ConfigureAwait(false);
            await _repository.RenewLeaseAsync(target.Id, _instanceId, options.LeaseTtl, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReconcileSubdomainsAsync(target, options, state, cancellationToken).ConfigureAwait(false);
        await _repository.SaveStateAsync(target.Id, _instanceId, state, serializedConfiguration, cancellationToken).ConfigureAwait(false);
        await _repository.RenewLeaseAsync(target.Id, _instanceId, options.LeaseTtl, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureEnumerationAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorOptions options,
        ReconOrchestratorState state,
        CancellationToken cancellationToken)
    {
        foreach (var provider in options.EnumerationProviders)
        {
            var providerName = provider.Trim().ToLowerInvariant();
            if (providerName.Length == 0)
            {
                continue;
            }

            var recordedRun = await _repository.GetProviderRunAsync(target.Id, providerName, cancellationToken).ConfigureAwait(false);
            if (recordedRun is not null)
            {
                state.ProviderRuns[providerName] = new ProviderRunState
                {
                    Provider = providerName,
                    Status = recordedRun.Status,
                    StartedAtUtc = recordedRun.StartedAtUtc,
                    CompletedAtUtc = recordedRun.CompletedAtUtc,
                    LastRequestedEventId = recordedRun.LastRequestedEventId,
                    Error = recordedRun.Error
                };

                if (recordedRun.Status is "Requested" or "Running" or "Completed")
                {
                    continue;
                }
            }

            var stateRun = state.ProviderRuns.GetValueOrDefault(providerName);
            if (stateRun is { Status: "Requested" or "Running" or "Completed" })
            {
                continue;
            }

            var eventId = Guid.NewGuid();
            await _repository.StartProviderRunAsync(target.Id, providerName, eventId, state.CorrelationId, cancellationToken).ConfigureAwait(false);

            var requested = new SubdomainEnumerationRequested(
                target.Id,
                target.RootDomain,
                providerName,
                "recon-orchestrator",
                DateTimeOffset.UtcNow,
                state.CorrelationId,
                eventId,
                producer: "argus-worker-recon-orchestrator");

            await _bus.Publish(requested, cancellationToken).ConfigureAwait(false);

            state.ProviderRuns[providerName] = new ProviderRunState
            {
                Provider = providerName,
                Status = "Requested",
                StartedAtUtc = DateTimeOffset.UtcNow,
                LastRequestedEventId = eventId
            };

            _logger.LogInformation(
                "ReconOrchestrator requested {Provider} enumeration for target {TargetId}.",
                providerName,
                target.Id);
        }
    }

    private async Task ReconcileSubdomainsAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorOptions options,
        ReconOrchestratorState state,
        CancellationToken cancellationToken)
    {
        var subdomains = await _repository.ListSubdomainsAsync(
            target.Id,
            target.RootDomain,
            cancellationToken).ConfigureAwait(false);

        foreach (var subdomain in subdomains)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = await _repository.GetSubdomainUrlProgressAsync(
                target.Id,
                subdomain,
                cancellationToken).ConfigureAwait(false);

            var spiderStatus = ResolveSpiderStatus(progress);
            var subdomainState = state.Subdomains.GetValueOrDefault(subdomain) ?? new SubdomainReconState
            {
                Subdomain = subdomain
            };

            subdomainState.TotalUrlAssets = progress.TotalUrlAssets;
            subdomainState.PendingUrlAssets = progress.PendingUrlAssets;
            subdomainState.ConfirmedUrlAssets = progress.ConfirmedUrlAssets;
            subdomainState.SpiderStatus = spiderStatus;
            subdomainState.LastCheckedAtUtc = DateTimeOffset.UtcNow;
            state.Subdomains[subdomain] = subdomainState;

            await _repository.UpsertSubdomainStatusAsync(
                target.Id,
                subdomain,
                progress,
                spiderStatus,
                cancellationToken).ConfigureAwait(false);

            var machineIdentity = ResolveMachineIdentity(options);
            var profile = _profilePlanner.GetOrCreateProfile(
                target.Id,
                subdomain,
                machineIdentity,
                state.Profiles);

            var profileJson = JsonSerializer.Serialize(profile, JsonOptions);
            var headerOrderJson = JsonSerializer.Serialize(profile.HeaderOrder, JsonOptions);

            await _repository.SaveProfileAssignmentAsync(
                target.Id,
                subdomain,
                machineIdentity,
                profile.ProfileId,
                profileJson,
                headerOrderJson,
                cancellationToken).ConfigureAwait(false);

            if (options.PublishSpiderSeeds && progress.TotalUrlAssets == 0)
            {
                await PublishSeedUrlsAsync(target, state, subdomain, profile, cancellationToken).ConfigureAwait(false);
            }
            else if (options.PublishPendingUrlResumes && progress.PendingUrlAssets > 0)
            {
                await PublishPendingUrlResumeAsync(target, state, subdomain, profile, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishSeedUrlsAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorState state,
        string subdomain,
        ReconWorkerProfile profile,
        CancellationToken cancellationToken)
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            var url = $"{scheme}://{subdomain}/";
            var message = new AssetDiscovered(
                target.Id,
                target.RootDomain,
                target.GlobalMaxDepth,
                0,
                AssetKind.Url,
                url,
                "recon-orchestrator",
                DateTimeOffset.UtcNow,
                state.CorrelationId,
                AssetAdmissionStage.Raw,
                null,
                DiscoveryContext: $"seed:{profile.ProfileId}",
                RelationshipPropertiesJson: JsonSerializer.Serialize(
                    new
                    {
                        recon_profile_id = profile.ProfileId,
                        profile.MachineIdentity,
                        rate_limit_rpm = profile.RequestsPerMinute,
                        profile.RandomDelayEnabled,
                        profile.RandomDelayMinSeconds,
                        profile.RandomDelayMaxSeconds
                    },
                    JsonOptions),
                EventId: Guid.NewGuid(),
                Producer: "argus-worker-recon-orchestrator");

            await _bus.Publish(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishPendingUrlResumeAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorState state,
        string subdomain,
        ReconWorkerProfile profile,
        CancellationToken cancellationToken)
    {
        var pending = await _repository.ListPendingUrlAssetsAsync(
            target.Id,
            subdomain,
            Math.Max(1, profile.RequestsPerMinute / 2),
            cancellationToken).ConfigureAwait(false);

        foreach (var asset in pending)
        {
            var message = new AssetDiscovered(
                target.Id,
                target.RootDomain,
                target.GlobalMaxDepth,
                asset.Depth,
                AssetKind.Url,
                asset.Url,
                "recon-orchestrator",
                DateTimeOffset.UtcNow,
                state.CorrelationId,
                AssetAdmissionStage.Indexed,
                asset.AssetId,
                DiscoveryContext: $"resume:{profile.ProfileId}",
                RelationshipPropertiesJson: JsonSerializer.Serialize(
                    new
                    {
                        recon_profile_id = profile.ProfileId,
                        profile.MachineIdentity,
                        rate_limit_rpm = profile.RequestsPerMinute,
                        profile.RandomDelayEnabled,
                        profile.RandomDelayMinSeconds,
                        profile.RandomDelayMaxSeconds
                    },
                    JsonOptions),
                EventId: Guid.NewGuid(),
                Producer: "argus-worker-recon-orchestrator");

            await _bus.Publish(message, cancellationToken).ConfigureAwait(false);
        }

        var stateEntry = state.Subdomains.GetValueOrDefault(subdomain);
        if (stateEntry is not null)
        {
            stateEntry.ResumeAssetIds = pending.Select(static item => item.AssetId).ToList();
        }
    }

    private static string ResolveSpiderStatus(SubdomainUrlProgress progress)
    {
        if (progress.TotalUrlAssets == 0)
        {
            return "NotStarted";
        }

        return progress.PendingUrlAssets == 0 ? "Complete" : "Resumable";
    }

    private static string ResolveMachineIdentity(ReconOrchestratorOptions options)
    {
        return Environment.GetEnvironmentVariable("ARGUS_WORKER_PUBLIC_IP")
            ?? Environment.GetEnvironmentVariable("POD_IP")
            ?? Environment.GetEnvironmentVariable("HOSTNAME")
            ?? options.DefaultMachineIdentity;
    }
}
