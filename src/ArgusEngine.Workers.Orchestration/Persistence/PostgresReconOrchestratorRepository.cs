using System.Data;
using System.Globalization;
using System.Text.Json;
using ArgusEngine.Workers.Orchestration.Configuration;
using ArgusEngine.Workers.Orchestration.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace ArgusEngine.Workers.Orchestration.Persistence;

public sealed class PostgresReconOrchestratorRepository : IReconOrchestratorRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly ILogger<PostgresReconOrchestratorRepository> _logger;

    public PostgresReconOrchestratorRepository(
        IConfiguration configuration,
        ILogger<PostgresReconOrchestratorRepository> logger)
    {
        _logger = logger;
        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? configuration.GetConnectionString("Postgres")
            ?? configuration["ConnectionStrings:Argus"]
            ?? configuration["Argus:Postgres:ConnectionString"]
            ?? configuration["Database:ConnectionString"]
            ?? configuration["ARGUS_CONNECTION_STRING"]
            ?? Environment.GetEnvironmentVariable("ARGUS_CONNECTION_STRING")
            ?? throw new InvalidOperationException("No PostgreSQL connection string configured.");
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists recon_orchestrator_states (
                target_id uuid primary key,
                orchestrator_type text not null,
                configuration_json jsonb not null,
                state_json jsonb not null,
                state_version integer not null default 1,
                active_instance_id uuid null,
                lease_expires_at_utc timestamptz null,
                created_at_utc timestamptz not null default now(),
                updated_at_utc timestamptz not null default now()
            );

            create table if not exists recon_orchestrator_provider_runs (
                target_id uuid not null,
                provider text not null,
                status text not null,
                started_at_utc timestamptz null,
                completed_at_utc timestamptz null,
                last_requested_event_id uuid null,
                correlation_id uuid not null,
                error text null,
                updated_at_utc timestamptz not null default now(),
                primary key (target_id, provider)
            );

            create table if not exists recon_orchestrator_subdomain_statuses (
                target_id uuid not null,
                subdomain text not null,
                spider_status text not null,
                total_url_assets integer not null default 0,
                pending_url_assets integer not null default 0,
                confirmed_url_assets integer not null default 0,
                first_seen_at_utc timestamptz not null default now(),
                last_checked_at_utc timestamptz not null default now(),
                primary key (target_id, subdomain)
            );

            create table if not exists recon_orchestrator_profile_assignments (
                target_id uuid not null,
                subdomain text not null,
                machine_identity text not null,
                profile_id text not null,
                profile_json jsonb not null,
                header_order_json jsonb not null,
                assigned_at_utc timestamptz not null default now(),
                last_seen_at_utc timestamptz not null default now(),
                primary key (target_id, subdomain, machine_identity)
            );

            create index if not exists ix_recon_orchestrator_states_lease
                on recon_orchestrator_states (lease_expires_at_utc, active_instance_id);

            create index if not exists ix_recon_orchestrator_profile_assignments_profile
                on recon_orchestrator_profile_assignments (profile_id);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReconTargetSnapshot>> ListTargetsAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, root_domain, global_max_depth
            from recon_targets
            order by created_at_utc
            limit @limit
            """;

        var targets = new List<ReconTargetSnapshot>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", Math.Max(1, limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            targets.Add(new ReconTargetSnapshot(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return targets;
    }

    public async Task<IReadOnlyList<ReconTargetSnapshot>> ResolveTargetsAsync(
        IReadOnlyCollection<Guid> targetIds,
        CancellationToken cancellationToken)
    {
        if (targetIds.Count == 0)
        {
            return [];
        }

        const string sql = """
            select id, root_domain, global_max_depth
            from recon_targets
            where id = any(@targetIds)
            order by created_at_utc
            """;

        var targets = new List<ReconTargetSnapshot>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetIds", targetIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            targets.Add(new ReconTargetSnapshot(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return targets;
    }

    public async Task<bool> TryAcquireLeaseAsync(
        Guid targetId,
        Guid instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into recon_orchestrator_states (
                target_id,
                orchestrator_type,
                configuration_json,
                state_json,
                active_instance_id,
                lease_expires_at_utc)
            values (
                @targetId,
                'ReconOrchestrator',
                '{}'::jsonb,
                '{}'::jsonb,
                @instanceId,
                now() + (@leaseSeconds || ' seconds')::interval)
            on conflict (target_id) do update
            set active_instance_id = excluded.active_instance_id,
                lease_expires_at_utc = excluded.lease_expires_at_utc,
                updated_at_utc = now()
            where recon_orchestrator_states.active_instance_id is null
               or recon_orchestrator_states.active_instance_id = @instanceId
               or recon_orchestrator_states.lease_expires_at_utc is null
               or recon_orchestrator_states.lease_expires_at_utc < now()
            returning target_id
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.AddWithValue("leaseSeconds", leaseTtl.TotalSeconds.ToString(CultureInfo.InvariantCulture));

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null && result != DBNull.Value;
    }

    public async Task RenewLeaseAsync(
        Guid targetId,
        Guid instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update recon_orchestrator_states
            set lease_expires_at_utc = now() + (@leaseSeconds || ' seconds')::interval,
                updated_at_utc = now()
            where target_id = @targetId
              and active_instance_id = @instanceId
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.AddWithValue("leaseSeconds", leaseTtl.TotalSeconds.ToString(CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconOrchestratorState> LoadOrCreateStateAsync(
        ReconTargetSnapshot target,
        ReconOrchestratorOptions options,
        string serializedConfiguration,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            select state_json
            from recon_orchestrator_states
            where target_id = @targetId
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var selectCommand = new NpgsqlCommand(selectSql, connection))
        {
            selectCommand.Parameters.AddWithValue("targetId", target.Id);
            var existingJson = await selectCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

            if (!string.IsNullOrWhiteSpace(existingJson) && existingJson != "{}")
            {
                var state = JsonSerializer.Deserialize<ReconOrchestratorState>(existingJson, JsonOptions);
                if (state is not null)
                {
                    return state;
                }
            }
        }

        var created = new ReconOrchestratorState
        {
            TargetId = target.Id,
            RootDomain = target.RootDomain,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        foreach (var provider in options.EnumerationProviders)
        {
            created.ProviderRuns[provider] = new ProviderRunState
            {
                Provider = provider,
                Status = "NotStarted"
            };
        }

        await SaveStateAsync(target.Id, Guid.Empty, created, serializedConfiguration, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task SaveStateAsync(
        Guid targetId,
        Guid instanceId,
        ReconOrchestratorState state,
        string serializedConfiguration,
        CancellationToken cancellationToken)
    {
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var stateJson = JsonSerializer.Serialize(state, JsonOptions);

        const string sql = """
            update recon_orchestrator_states
            set configuration_json = @configurationJson,
                state_json = @stateJson,
                state_version = @stateVersion,
                updated_at_utc = now()
            where target_id = @targetId
              and (@instanceId = '00000000-0000-0000-0000-000000000000'::uuid or active_instance_id = @instanceId)
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("instanceId", instanceId);
        command.Parameters.Add(JsonParameter("configurationJson", serializedConfiguration));
        command.Parameters.Add(JsonParameter("stateJson", stateJson));
        command.Parameters.AddWithValue("stateVersion", state.Version);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            _logger.LogWarning("ReconOrchestrator state save skipped for target {TargetId}; lease no longer owned by instance {InstanceId}.", targetId, instanceId);
        }
    }

    public async Task<ProviderRunSnapshot?> GetProviderRunAsync(
        Guid targetId,
        string provider,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select target_id, provider, status, started_at_utc, completed_at_utc, last_requested_event_id, correlation_id, error
            from recon_orchestrator_provider_runs
            where target_id = @targetId
              and lower(provider) = lower(@provider)
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("provider", provider);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProviderRunSnapshot(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task StartProviderRunAsync(
        Guid targetId,
        string provider,
        Guid eventId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into recon_orchestrator_provider_runs (
                target_id,
                provider,
                status,
                started_at_utc,
                last_requested_event_id,
                correlation_id)
            values (
                @targetId,
                @provider,
                'Requested',
                now(),
                @eventId,
                @correlationId)
            on conflict (target_id, provider) do update
            set status = case
                    when recon_orchestrator_provider_runs.status in ('Completed', 'Running', 'Requested') then recon_orchestrator_provider_runs.status
                    else 'Requested'
                end,
                started_at_utc = coalesce(recon_orchestrator_provider_runs.started_at_utc, excluded.started_at_utc),
                last_requested_event_id = case
                    when recon_orchestrator_provider_runs.status in ('Completed', 'Running', 'Requested') then recon_orchestrator_provider_runs.last_requested_event_id
                    else excluded.last_requested_event_id
                end,
                correlation_id = recon_orchestrator_provider_runs.correlation_id,
                updated_at_utc = now()
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("correlationId", correlationId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListSubdomainsAsync(
        Guid targetId,
        string rootDomain,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with candidate_assets as (
                select lower(coalesce(nullif(canonical_key, ''), nullif(raw_value, ''), final_url)) as value
                from stored_assets
                where target_id = @targetId
                  and (
                        lower(coalesce(canonical_key, '')) like @suffix
                     or lower(coalesce(raw_value, '')) like @suffix
                     or lower(coalesce(final_url, '')) like @urlSuffix
                  )
            )
            select distinct regexp_replace(regexp_replace(value, '^https?://', ''), '[:/].*$', '') as subdomain
            from candidate_assets
            where value is not null
              and value not like 'http://%'
              and value not like 'https://%'
              and value <> lower(@rootDomain)
              and value like @suffix
            order by 1
            """;

        var root = rootDomain.Trim().Trim('.').ToLowerInvariant();
        var subdomains = new List<string>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("rootDomain", root);
        command.Parameters.AddWithValue("suffix", "%." + root);
        command.Parameters.AddWithValue("urlSuffix", "%://" + "%." + root + "%");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var subdomain = reader.GetString(0).Trim().Trim('.').ToLowerInvariant();
            if (subdomain.Length > 0 && subdomain.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))
            {
                subdomains.Add(subdomain);
            }
        }

        return subdomains;
    }

    public async Task<SubdomainUrlProgress> GetSubdomainUrlProgressAsync(
        Guid targetId,
        string subdomain,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                count(*)::int as total_url_assets,
                count(*) filter (
                    where lower(coalesce(lifecycle_status::text, '')) not in (
                        'confirmed',
                        'complete',
                        'completed',
                        'fetched',
                        'indexed',
                        'verified'
                    )
                )::int as pending_url_assets,
                count(*) filter (
                    where lower(coalesce(lifecycle_status::text, '')) in (
                        'confirmed',
                        'complete',
                        'completed',
                        'fetched',
                        'indexed',
                        'verified'
                    )
                )::int as confirmed_url_assets
            from stored_assets
            where target_id = @targetId
              and (
                    lower(coalesce(raw_value, '')) like @httpsPrefix
                 or lower(coalesce(raw_value, '')) like @httpPrefix
                 or lower(coalesce(canonical_key, '')) like @httpsPrefix
                 or lower(coalesce(canonical_key, '')) like @httpPrefix
                 or lower(coalesce(final_url, '')) like @httpsPrefix
                 or lower(coalesce(final_url, '')) like @httpPrefix
              )
            """;

        var host = subdomain.Trim().Trim('.').ToLowerInvariant();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("httpsPrefix", "https://" + host + "%");
        command.Parameters.AddWithValue("httpPrefix", "http://" + host + "%");

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SubdomainUrlProgress(host, 0, 0, 0);
        }

        return new SubdomainUrlProgress(
            host,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    public async Task<IReadOnlyList<PendingUrlAsset>> ListPendingUrlAssetsAsync(
        Guid targetId,
        string subdomain,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id,
                   coalesce(nullif(final_url, ''), nullif(raw_value, ''), canonical_key) as url,
                   coalesce(depth, 0) as depth
            from stored_assets
            where target_id = @targetId
              and (
                    lower(coalesce(raw_value, '')) like @httpsPrefix
                 or lower(coalesce(raw_value, '')) like @httpPrefix
                 or lower(coalesce(canonical_key, '')) like @httpsPrefix
                 or lower(coalesce(canonical_key, '')) like @httpPrefix
                 or lower(coalesce(final_url, '')) like @httpsPrefix
                 or lower(coalesce(final_url, '')) like @httpPrefix
              )
              and lower(coalesce(lifecycle_status::text, '')) not in (
                    'confirmed',
                    'complete',
                    'completed',
                    'fetched',
                    'indexed',
                    'verified'
              )
            order by coalesce(last_seen_at_utc, discovered_at_utc, now())
            limit @limit
            """;

        var host = subdomain.Trim().Trim('.').ToLowerInvariant();
        var urls = new List<PendingUrlAsset>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("httpsPrefix", "https://" + host + "%");
        command.Parameters.AddWithValue("httpPrefix", "http://" + host + "%");
        command.Parameters.AddWithValue("limit", Math.Max(1, limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            urls.Add(new PendingUrlAsset(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return urls;
    }

    public async Task UpsertSubdomainStatusAsync(
        Guid targetId,
        string subdomain,
        SubdomainUrlProgress progress,
        string spiderStatus,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into recon_orchestrator_subdomain_statuses (
                target_id,
                subdomain,
                spider_status,
                total_url_assets,
                pending_url_assets,
                confirmed_url_assets)
            values (
                @targetId,
                @subdomain,
                @spiderStatus,
                @totalUrlAssets,
                @pendingUrlAssets,
                @confirmedUrlAssets)
            on conflict (target_id, subdomain) do update
            set spider_status = excluded.spider_status,
                total_url_assets = excluded.total_url_assets,
                pending_url_assets = excluded.pending_url_assets,
                confirmed_url_assets = excluded.confirmed_url_assets,
                last_checked_at_utc = now()
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("subdomain", subdomain);
        command.Parameters.AddWithValue("spiderStatus", spiderStatus);
        command.Parameters.AddWithValue("totalUrlAssets", progress.TotalUrlAssets);
        command.Parameters.AddWithValue("pendingUrlAssets", progress.PendingUrlAssets);
        command.Parameters.AddWithValue("confirmedUrlAssets", progress.ConfirmedUrlAssets);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveProfileAssignmentAsync(
        Guid targetId,
        string subdomain,
        string machineIdentity,
        string profileId,
        string profileJson,
        string headerOrderJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into recon_orchestrator_profile_assignments (
                target_id,
                subdomain,
                machine_identity,
                profile_id,
                profile_json,
                header_order_json)
            values (
                @targetId,
                @subdomain,
                @machineIdentity,
                @profileId,
                @profileJson,
                @headerOrderJson)
            on conflict (target_id, subdomain, machine_identity) do update
            set profile_id = recon_orchestrator_profile_assignments.profile_id,
                profile_json = recon_orchestrator_profile_assignments.profile_json,
                header_order_json = recon_orchestrator_profile_assignments.header_order_json,
                last_seen_at_utc = now()
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("targetId", targetId);
        command.Parameters.AddWithValue("subdomain", subdomain);
        command.Parameters.AddWithValue("machineIdentity", machineIdentity);
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.Add(JsonParameter("profileJson", profileJson));
        command.Parameters.Add(JsonParameter("headerOrderJson", headerOrderJson));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static NpgsqlParameter JsonParameter(string name, string value)
    {
        return new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = value
        };
    }
}
