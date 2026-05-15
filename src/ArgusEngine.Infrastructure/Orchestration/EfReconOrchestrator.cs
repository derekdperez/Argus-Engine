using System.Text.Json;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Orchestration;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Infrastructure.Orchestration;

public sealed class EfReconOrchestrator(
    IDbContextFactory<ArgusDbContext> dbFactory,
    IEventOutbox outbox,
    IOptions<ReconOrchestratorOptions> options,
    ILogger<EfReconOrchestrator> logger) : IReconOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly string[] Providers = ["subfinder", "amass"];

    public async Task<ReconOrchestratorSnapshot> AttachToTargetAsync(
        Guid targetId,
        string attachedBy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

        var target = await db.Targets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == targetId, cancellationToken)
            .ConfigureAwait(false);

        if (target is null)
        {
            throw new InvalidOperationException($"Target {targetId} was not found.");
        }

        var config = ReconOrchestratorConfiguration.FromOptions(options.Value);
        var now = DateTimeOffset.UtcNow;
        var configJson = JsonSerializer.Serialize(config, JsonOptions);
        var stateJson = BuildInitialStateJson(target, config, now);

        await ReconDbCommands.ExecuteAsync(
            db,
            """
            INSERT INTO recon_orchestrator_states
                (target_id, orchestrator_type, status, config_json, state_json, attached_by, attached_at_utc, started_at_utc, updated_at_utc)
            VALUES
                (@target_id, 'ReconOrchestrator', 'active', CAST(@config_json AS jsonb), CAST(@state_json AS jsonb), @attached_by, @now, @now, @now)
            ON CONFLICT (target_id) DO UPDATE SET
                status = CASE
                    WHEN recon_orchestrator_states.status = 'completed' THEN 'active'
                    ELSE recon_orchestrator_states.status
                END,
                config_json = EXCLUDED.config_json,
                state_json = recon_orchestrator_states.state_json || EXCLUDED.state_json,
                attached_by = EXCLUDED.attached_by,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = target.Id,
                ["config_json"] = configJson,
                ["state_json"] = stateJson,
                ["attached_by"] = string.IsNullOrWhiteSpace(attachedBy) ? "system" : attachedBy.Trim(),
                ["now"] = now
            },
            cancellationToken).ConfigureAwait(false);

        return new ReconOrchestratorSnapshot(target.Id, target.RootDomain, "active", config, now, now);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveTargetIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

        return await ReconDbCommands.QueryAsync(
            db,
            """
            SELECT target_id
            FROM recon_orchestrator_states
            WHERE status = 'active'
            ORDER BY updated_at_utc ASC;
            """,
            new Dictionary<string, object?>(),
            reader => reader.GetGuid(reader.GetOrdinal("target_id")),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconOrchestratorTickResult> TickTargetAsync(
        Guid targetId,
        string tickOwner,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return new ReconOrchestratorTickResult(targetId, false, 0, 0, 0, 0, false);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

        var owner = string.IsNullOrWhiteSpace(tickOwner)
            ? $"recon-orchestrator-{Environment.MachineName}"
            : tickOwner.Trim();

        var claimed = await TryClaimAsync(db, targetId, owner, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            return new ReconOrchestratorTickResult(targetId, false, 0, 0, 0, 0, false);
        }

        try
        {
            var target = await db.Targets.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == targetId, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                await CompleteAsync(db, targetId, owner, "target_missing", cancellationToken).ConfigureAwait(false);
                return new ReconOrchestratorTickResult(targetId, true, 0, 0, 0, 0, true);
            }

            var config = await LoadConfigurationAsync(db, targetId, cancellationToken).ConfigureAwait(false);
            var providersQueued = await EnsureProviderRunsAsync(db, target, cancellationToken).ConfigureAwait(false);
            var subdomainResult = await ReconcileSubdomainsAsync(db, target, config, cancellationToken).ConfigureAwait(false);
            var providerStatuses = await LoadProviderStatusesAsync(db, targetId, cancellationToken).ConfigureAwait(false);
            var allProvidersFinished = Providers.All(provider => providerStatuses.TryGetValue(provider, out var status)
                && status is "completed" or "failed");
            var completed = allProvidersFinished && subdomainResult.IncompleteSubdomains == 0;

            await UpdateStateAsync(
                db,
                target,
                owner,
                providerStatuses,
                subdomainResult,
                completed,
                cancellationToken).ConfigureAwait(false);

            return new ReconOrchestratorTickResult(
                targetId,
                true,
                providersQueued,
                subdomainResult.SubdomainsChecked,
                subdomainResult.SeedsQueued,
                subdomainResult.IncompleteSubdomains,
                completed);
        }
        finally
        {
            await ReleaseClaimAsync(db, targetId, owner, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryClaimAsync(
        ArgusDbContext db,
        Guid targetId,
        string owner,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddSeconds(Math.Clamp(options.Value.ClaimTimeoutSeconds, 15, 3600));
        var rows = await ReconDbCommands.ExecuteAsync(
            db,
            """
            UPDATE recon_orchestrator_states
            SET lease_owner = @owner,
                lease_until_utc = @lease_until,
                updated_at_utc = @now
            WHERE target_id = @target_id
              AND status = 'active'
              AND (lease_until_utc IS NULL OR lease_until_utc <= @now OR lease_owner = @owner);
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = targetId,
                ["owner"] = owner,
                ["lease_until"] = leaseUntil,
                ["now"] = now
            },
            cancellationToken).ConfigureAwait(false);

        return rows > 0;
    }

    private async Task<int> EnsureProviderRunsAsync(
        ArgusDbContext db,
        ReconTarget target,
        CancellationToken cancellationToken)
    {
        var queued = 0;
        foreach (var provider in Providers)
        {
            if (await HasProviderRunAsync(db, target.Id, provider, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var correlation = NewId.NextGuid();
            var eventId = NewId.NextGuid();
            var now = DateTimeOffset.UtcNow;

            await ReconDbCommands.ExecuteAsync(
                db,
                """
                INSERT INTO recon_orchestrator_provider_runs
                    (id, target_id, provider, status, requested_at_utc, correlation_id, event_id, updated_at_utc)
                VALUES
                    (@id, @target_id, @provider, 'requested', @now, @correlation_id, @event_id, @now)
                ON CONFLICT (target_id, provider) DO NOTHING;
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid(),
                    ["target_id"] = target.Id,
                    ["provider"] = provider,
                    ["correlation_id"] = correlation,
                    ["event_id"] = eventId,
                    ["now"] = now
                },
                cancellationToken).ConfigureAwait(false);

            await outbox.EnqueueAsync(
                new SubdomainEnumerationRequested(
                    target.Id,
                    target.RootDomain,
                    provider,
                    "recon-orchestrator",
                    now,
                    correlation,
                    EventId: eventId,
                    CausationId: correlation,
                    Producer: "recon-orchestrator"),
                cancellationToken).ConfigureAwait(false);

            queued++;
        }

        return queued;
    }

    private async Task<bool> HasProviderRunAsync(
        ArgusDbContext db,
        Guid targetId,
        string provider,
        CancellationToken cancellationToken)
    {
        var rowCount = await ReconDbCommands.ScalarAsync<long>(
            db,
            """
            SELECT COUNT(*)
            FROM recon_orchestrator_provider_runs
            WHERE target_id = @target_id AND provider = @provider;
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = targetId,
                ["provider"] = provider
            },
            cancellationToken).ConfigureAwait(false);

        if (rowCount > 0)
        {
            return true;
        }

        var discoveredBy = $"enum-worker:{provider}";
        var alreadyDiscovered = await db.Assets.AsNoTracking()
            .AnyAsync(
                a => a.TargetId == targetId
                    && a.Kind == AssetKind.Subdomain
                    && a.DiscoveredBy == discoveredBy,
                cancellationToken)
            .ConfigureAwait(false);

        if (!alreadyDiscovered)
        {
            return false;
        }

        await ReconDbCommands.ExecuteAsync(
            db,
            """
            INSERT INTO recon_orchestrator_provider_runs
                (id, target_id, provider, status, requested_at_utc, started_at_utc, completed_at_utc, updated_at_utc)
            VALUES
                (@id, @target_id, @provider, 'completed', @now, @now, @now, @now)
            ON CONFLICT (target_id, provider) DO NOTHING;
            """,
            new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["target_id"] = targetId,
                ["provider"] = provider,
                ["now"] = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<SubdomainReconcileResult> ReconcileSubdomainsAsync(
        ArgusDbContext db,
        ReconTarget target,
        ReconOrchestratorConfiguration config,
        CancellationToken cancellationToken)
    {
        var subdomains = await db.Assets.AsNoTracking()
            .Where(a => a.TargetId == target.Id
                && a.Kind == AssetKind.Subdomain
                && a.LifecycleStatus == AssetLifecycleStatus.Confirmed)
            .Select(a => new SubdomainAsset(a.Id, a.RawValue, a.CanonicalKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var checkedCount = 0;
        var incompleteCount = 0;
        var seedsQueued = 0;

        foreach (var subdomain in subdomains)
        {
            var subdomainKey = NormalizeHost(string.IsNullOrWhiteSpace(subdomain.RawValue) ? subdomain.CanonicalKey : subdomain.RawValue);
            if (string.IsNullOrWhiteSpace(subdomainKey))
            {
                continue;
            }

            var urlStats = await LoadUrlStatsAsync(db, target.Id, subdomainKey, cancellationToken).ConfigureAwait(false);
            var queueStats = await LoadQueueStatsAsync(db, target.Id, subdomainKey, cancellationToken).ConfigureAwait(false);
            var hasAnyUrlEvidence = urlStats.Total > 0 || queueStats.Total > 0;
            var status = ResolveSubdomainStatus(urlStats, queueStats, hasAnyUrlEvidence);

            if (!hasAnyUrlEvidence)
            {
                seedsQueued += await QueueSubdomainSeedsAsync(target, subdomain.Id, subdomainKey, cancellationToken).ConfigureAwait(false);
                status = "queued";
                incompleteCount++;
            }
            else if (status != "complete")
            {
                incompleteCount++;
            }

            await UpsertSubdomainStateAsync(db, target.Id, subdomain.Id, subdomainKey, status, urlStats, queueStats, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSubdomainProfilePlaceholdersAsync(db, target.Id, subdomainKey, config, cancellationToken).ConfigureAwait(false);
            checkedCount++;
        }

        return new SubdomainReconcileResult(checkedCount, seedsQueued, incompleteCount);
    }

    private async Task<int> QueueSubdomainSeedsAsync(
        ReconTarget target,
        Guid subdomainAssetId,
        string subdomainKey,
        CancellationToken cancellationToken)
    {
        var queued = 0;
        var correlation = NewId.NextGuid();
        foreach (var rootUrl in RootUrls(subdomainKey))
        {
            await outbox.EnqueueAsync(
                new AssetDiscovered(
                    target.Id,
                    target.RootDomain,
                    target.GlobalMaxDepth,
                    Depth: 1,
                    Kind: AssetKind.Url,
                    RawValue: rootUrl,
                    DiscoveredBy: "recon-orchestrator",
                    OccurredAt: DateTimeOffset.UtcNow,
                    CorrelationId: correlation,
                    AdmissionStage: AssetAdmissionStage.Raw,
                    AssetId: null,
                    DiscoveryContext: $"ReconOrchestrator spider seed for subdomain={subdomainKey}",
                    ParentAssetId: subdomainAssetId,
                    RelationshipType: AssetRelationshipType.Contains,
                    IsPrimaryRelationship: true,
                    EventId: NewId.NextGuid(),
                    CausationId: correlation,
                    Producer: "recon-orchestrator"),
                cancellationToken).ConfigureAwait(false);
            queued++;
        }

        return queued;
    }

    private async Task EnsureSubdomainProfilePlaceholdersAsync(
        ArgusDbContext db,
        Guid targetId,
        string subdomainKey,
        ReconOrchestratorConfiguration config,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < Math.Max(1, config.ReconProfilesPerSubdomain); i++)
        {
            var machineKey = $"recon-profile-{i:00}";
            var generated = ReconProfileFactory.Create(config, targetId, subdomainKey, machineKey, i);
            await ReconDbCommands.ExecuteAsync(
                db,
                """
                INSERT INTO recon_orchestrator_profile_assignments
                    (id, target_id, subdomain_key, machine_key, machine_name, public_ip_address,
                     profile_index, device_type, browser, operating_system, hardware_age_years,
                     user_agent, accept_language, headers_json, header_order_seed,
                     random_delay_enabled, random_delay_min_ms, random_delay_max_ms,
                     requests_per_minute_per_subdomain, request_count, created_at_utc, updated_at_utc)
                VALUES
                    (@id, @target_id, @subdomain_key, @machine_key, @machine_name, NULL,
                     @profile_index, @device_type, @browser, @operating_system, @hardware_age_years,
                     @user_agent, @accept_language, CAST(@headers_json AS jsonb), @header_order_seed,
                     @random_delay_enabled, @random_delay_min_ms, @random_delay_max_ms,
                     @requests_per_minute_per_subdomain, 0, @now, @now)
                ON CONFLICT (target_id, subdomain_key, machine_key) DO NOTHING;
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid(),
                    ["target_id"] = targetId,
                    ["subdomain_key"] = subdomainKey,
                    ["machine_key"] = machineKey,
                    ["machine_name"] = "orchestrator-reserved-profile",
                    ["profile_index"] = generated.ProfileIndex,
                    ["device_type"] = generated.DeviceType,
                    ["browser"] = generated.Browser,
                    ["operating_system"] = generated.OperatingSystem,
                    ["hardware_age_years"] = generated.HardwareAgeYears,
                    ["user_agent"] = generated.UserAgent,
                    ["accept_language"] = generated.AcceptLanguage,
                    ["headers_json"] = generated.HeadersJson,
                    ["header_order_seed"] = generated.HeaderOrderSeed,
                    ["random_delay_enabled"] = generated.RandomDelayEnabled,
                    ["random_delay_min_ms"] = generated.RandomDelayMinMs,
                    ["random_delay_max_ms"] = generated.RandomDelayMaxMs,
                    ["requests_per_minute_per_subdomain"] = generated.RequestsPerMinutePerSubdomain,
                    ["now"] = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<UrlStats> LoadUrlStatsAsync(
        ArgusDbContext db,
        Guid targetId,
        string subdomainKey,
        CancellationToken cancellationToken)
    {
        var hostPattern = $"%://{subdomainKey}/%";
        var hostPortPattern = $"%://{subdomainKey}:%";
        var bareHostPattern = $"%://{subdomainKey}";

        var query = db.Assets.AsNoTracking()
            .Where(a => a.TargetId == targetId && a.Kind == AssetKind.Url)
            .Where(a => EF.Functions.ILike(a.RawValue, hostPattern)
                || EF.Functions.ILike(a.RawValue, hostPortPattern)
                || EF.Functions.ILike(a.RawValue, bareHostPattern)
                || EF.Functions.ILike(a.CanonicalKey, hostPattern)
                || EF.Functions.ILike(a.CanonicalKey, hostPortPattern)
                || EF.Functions.ILike(a.CanonicalKey, bareHostPattern));

        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var confirmed = await query.LongCountAsync(a => a.LifecycleStatus == AssetLifecycleStatus.Confirmed, cancellationToken)
            .ConfigureAwait(false);

        return new UrlStats(total, confirmed, Math.Max(0, total - confirmed));
    }

    private static async Task<QueueStats> LoadQueueStatsAsync(
        ArgusDbContext db,
        Guid targetId,
        string subdomainKey,
        CancellationToken cancellationToken)
    {
        var rows = await db.HttpRequestQueue.AsNoTracking()
            .Where(q => q.TargetId == targetId && q.DomainKey == subdomainKey)
            .GroupBy(_ => 1)
            .Select(g => new QueueStats(
                g.LongCount(),
                g.LongCount(q => q.State == HttpRequestQueueState.Queued || q.State == HttpRequestQueueState.Retry),
                g.LongCount(q => q.State == HttpRequestQueueState.InFlight),
                g.LongCount(q => q.State == HttpRequestQueueState.Failed)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.FirstOrDefault() ?? new QueueStats(0, 0, 0, 0);
    }

    private static string ResolveSubdomainStatus(UrlStats urlStats, QueueStats queueStats, bool hasAnyUrlEvidence)
    {
        if (!hasAnyUrlEvidence)
        {
            return "not_started";
        }

        if (urlStats.Unconfirmed > 0 || queueStats.Pending > 0 || queueStats.InFlight > 0)
        {
            return "running";
        }

        return "complete";
    }

    private static async Task UpsertSubdomainStateAsync(
        ArgusDbContext db,
        Guid targetId,
        Guid subdomainAssetId,
        string subdomainKey,
        string status,
        UrlStats urlStats,
        QueueStats queueStats,
        CancellationToken cancellationToken)
    {
        await ReconDbCommands.ExecuteAsync(
            db,
            """
            INSERT INTO recon_orchestrator_subdomain_states
                (id, target_id, subdomain_asset_id, subdomain_key, status,
                 confirmed_url_count, unconfirmed_url_count, pending_url_count, in_flight_url_count, failed_url_count,
                 last_checked_at_utc, updated_at_utc)
            VALUES
                (@id, @target_id, @subdomain_asset_id, @subdomain_key, @status,
                 @confirmed_url_count, @unconfirmed_url_count, @pending_url_count, @in_flight_url_count, @failed_url_count,
                 @now, @now)
            ON CONFLICT (target_id, subdomain_key) DO UPDATE SET
                subdomain_asset_id = EXCLUDED.subdomain_asset_id,
                status = EXCLUDED.status,
                confirmed_url_count = EXCLUDED.confirmed_url_count,
                unconfirmed_url_count = EXCLUDED.unconfirmed_url_count,
                pending_url_count = EXCLUDED.pending_url_count,
                in_flight_url_count = EXCLUDED.in_flight_url_count,
                failed_url_count = EXCLUDED.failed_url_count,
                last_checked_at_utc = EXCLUDED.last_checked_at_utc,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """,
            new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(),
                ["target_id"] = targetId,
                ["subdomain_asset_id"] = subdomainAssetId,
                ["subdomain_key"] = subdomainKey,
                ["status"] = status,
                ["confirmed_url_count"] = urlStats.Confirmed,
                ["unconfirmed_url_count"] = urlStats.Unconfirmed,
                ["pending_url_count"] = queueStats.Pending,
                ["in_flight_url_count"] = queueStats.InFlight,
                ["failed_url_count"] = queueStats.Failed,
                ["now"] = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReconOrchestratorConfiguration> LoadConfigurationAsync(
        ArgusDbContext db,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var rows = await ReconDbCommands.QueryAsync(
            db,
            """
            SELECT config_json::text AS config_json
            FROM recon_orchestrator_states
            WHERE target_id = @target_id
            LIMIT 1;
            """,
            new Dictionary<string, object?> { ["target_id"] = targetId },
            reader => reader.GetString(reader.GetOrdinal("config_json")),
            cancellationToken).ConfigureAwait(false);

        var json = rows.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<ReconOrchestratorConfiguration>(json, JsonOptions)
                    ?? ReconOrchestratorConfiguration.FromOptions(options.Value);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse recon orchestrator config for target {TargetId}; using defaults.", targetId);
            }
        }

        return ReconOrchestratorConfiguration.FromOptions(options.Value);
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadProviderStatusesAsync(
        ArgusDbContext db,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var rows = await ReconDbCommands.QueryAsync(
            db,
            """
            SELECT provider, status
            FROM recon_orchestrator_provider_runs
            WHERE target_id = @target_id;
            """,
            new Dictionary<string, object?> { ["target_id"] = targetId },
            reader => new KeyValuePair<string, string>(
                reader.GetString(reader.GetOrdinal("provider")),
                reader.GetString(reader.GetOrdinal("status"))),
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task UpdateStateAsync(
        ArgusDbContext db,
        ReconTarget target,
        string owner,
        IReadOnlyDictionary<string, string> providerStatuses,
        SubdomainReconcileResult subdomainResult,
        bool completed,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var stateJson = JsonSerializer.Serialize(
            new
            {
                targetId = target.Id,
                rootDomain = target.RootDomain,
                providerStatuses,
                subdomainsChecked = subdomainResult.SubdomainsChecked,
                subdomainSeedsQueued = subdomainResult.SeedsQueued,
                incompleteSubdomains = subdomainResult.IncompleteSubdomains,
                lastTickAtUtc = now,
                lastTickOwner = owner
            },
            JsonOptions);

        await ReconDbCommands.ExecuteAsync(
            db,
            """
            UPDATE recon_orchestrator_states
            SET state_json = CAST(@state_json AS jsonb),
                status = @status,
                last_tick_at_utc = @now,
                completed_at_utc = CASE WHEN @completed THEN @now ELSE completed_at_utc END,
                updated_at_utc = @now
            WHERE target_id = @target_id;
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = target.Id,
                ["state_json"] = stateJson,
                ["status"] = completed ? "completed" : "active",
                ["completed"] = completed,
                ["now"] = now
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteAsync(
        ArgusDbContext db,
        Guid targetId,
        string owner,
        string reason,
        CancellationToken cancellationToken)
    {
        var stateJson = JsonSerializer.Serialize(new { reason, owner, completedAtUtc = DateTimeOffset.UtcNow }, JsonOptions);
        await ReconDbCommands.ExecuteAsync(
            db,
            """
            UPDATE recon_orchestrator_states
            SET status = 'completed',
                state_json = CAST(@state_json AS jsonb),
                completed_at_utc = @now,
                updated_at_utc = @now
            WHERE target_id = @target_id;
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = targetId,
                ["state_json"] = stateJson,
                ["now"] = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReleaseClaimAsync(
        ArgusDbContext db,
        Guid targetId,
        string owner,
        CancellationToken cancellationToken)
    {
        await ReconDbCommands.ExecuteAsync(
            db,
            """
            UPDATE recon_orchestrator_states
            SET lease_owner = NULL,
                lease_until_utc = NULL,
                updated_at_utc = @now
            WHERE target_id = @target_id AND lease_owner = @owner;
            """,
            new Dictionary<string, object?>
            {
                ["target_id"] = targetId,
                ["owner"] = owner,
                ["now"] = DateTimeOffset.UtcNow
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildInitialStateJson(ReconTarget target, ReconOrchestratorConfiguration config, DateTimeOffset now)
    {
        var targetProfiles = Enumerable.Range(0, Math.Max(1, config.ReconProfilesPerTarget))
            .Select(i => ReconProfileFactory.Create(config, target.Id, target.RootDomain, $"target-profile-{i:00}", i))
            .Select(profile => new
            {
                profile.ProfileIndex,
                profile.DeviceType,
                profile.Browser,
                profile.OperatingSystem,
                profile.HardwareAgeYears,
                profile.UserAgent,
                profile.AcceptLanguage,
                profile.HeaderOrderSeed,
                profile.RandomDelayEnabled,
                profile.RandomDelayMinMs,
                profile.RandomDelayMaxMs,
                profile.RequestsPerMinutePerSubdomain
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                targetId = target.Id,
                rootDomain = target.RootDomain,
                attachedAtUtc = now,
                targetProfiles
            },
            JsonOptions);
    }

    private static IEnumerable<string> RootUrls(string host)
    {
        yield return $"https://{host}/";
        yield return $"http://{host}/";
    }

    private static string NormalizeHost(string value)
    {
        var host = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return uri.IdnHost.Trim().TrimEnd('.').ToLowerInvariant();
        }

        return host;
    }

    private sealed record SubdomainAsset(Guid Id, string RawValue, string CanonicalKey);

    private sealed record UrlStats(long Total, long Confirmed, long Unconfirmed);

    private sealed record QueueStats(long Total, long Pending, long InFlight, long Failed);

    private sealed record SubdomainReconcileResult(int SubdomainsChecked, int SeedsQueued, int IncompleteSubdomains);
}
