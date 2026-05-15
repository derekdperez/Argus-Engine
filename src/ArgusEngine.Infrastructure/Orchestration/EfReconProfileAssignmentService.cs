using System.Text.Json;
using ArgusEngine.Application.Orchestration;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Infrastructure.Orchestration;

public sealed class EfReconProfileAssignmentService(
    IDbContextFactory<ArgusDbContext> dbFactory,
    IOptions<ReconOrchestratorOptions> options,
    ILogger<EfReconProfileAssignmentService> logger) : IReconProfileAssignmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ReconHeaderProfile?> GetOrCreateProfileAsync(
        ReconProfileAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TargetId == Guid.Empty || string.IsNullOrWhiteSpace(request.SubdomainKey) || string.IsNullOrWhiteSpace(request.MachineKey))
        {
            return null;
        }

        var subdomainKey = NormalizeKey(request.SubdomainKey);
        var machineKey = NormalizeMachineKey(request.MachineKey);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await ReconOrchestratorSql.EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);
            var configuration = await LoadConfigurationAsync(db, request.TargetId, cancellationToken).ConfigureAwait(false);
            var profileIndex = ResolveProfileIndex(configuration, request.TargetId, subdomainKey, machineKey);
            var generated = ReconProfileFactory.Create(configuration, request.TargetId, subdomainKey, machineKey, profileIndex);
            var assignmentId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await ReconDbCommands.ExecuteAsync(
                db,
                """
                INSERT INTO recon_orchestrator_profile_assignments
                    (id, target_id, subdomain_key, machine_key, machine_name, public_ip_address,
                     profile_index, device_type, browser, operating_system, hardware_age_years,
                     user_agent, accept_language, headers_json, header_order_seed,
                     random_delay_enabled, random_delay_min_ms, random_delay_max_ms,
                     requests_per_minute_per_subdomain, request_count, created_at_utc, updated_at_utc, last_used_at_utc)
                VALUES
                    (@id, @target_id, @subdomain_key, @machine_key, @machine_name, @public_ip_address,
                     @profile_index, @device_type, @browser, @operating_system, @hardware_age_years,
                     @user_agent, @accept_language, CAST(@headers_json AS jsonb), @header_order_seed,
                     @random_delay_enabled, @random_delay_min_ms, @random_delay_max_ms,
                     @requests_per_minute_per_subdomain, 1, @now, @now, @now)
                ON CONFLICT (target_id, subdomain_key, machine_key) DO UPDATE SET
                    machine_name = COALESCE(EXCLUDED.machine_name, recon_orchestrator_profile_assignments.machine_name),
                    public_ip_address = COALESCE(EXCLUDED.public_ip_address, recon_orchestrator_profile_assignments.public_ip_address),
                    request_count = recon_orchestrator_profile_assignments.request_count + 1,
                    updated_at_utc = EXCLUDED.updated_at_utc,
                    last_used_at_utc = EXCLUDED.last_used_at_utc;
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = assignmentId,
                    ["target_id"] = request.TargetId,
                    ["subdomain_key"] = subdomainKey,
                    ["machine_key"] = machineKey,
                    ["machine_name"] = NullIfBlank(request.MachineName),
                    ["public_ip_address"] = NullIfBlank(request.PublicIpAddress),
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
                    ["now"] = now
                },
                cancellationToken).ConfigureAwait(false);

            var rows = await ReconDbCommands.QueryAsync(
                db,
                """
                SELECT id, profile_index, device_type, browser, operating_system, hardware_age_years,
                       headers_json::text AS headers_json, header_order_seed, random_delay_enabled,
                       random_delay_min_ms, random_delay_max_ms, requests_per_minute_per_subdomain
                FROM recon_orchestrator_profile_assignments
                WHERE target_id = @target_id AND subdomain_key = @subdomain_key AND machine_key = @machine_key
                LIMIT 1;
                """,
                new Dictionary<string, object?>
                {
                    ["target_id"] = request.TargetId,
                    ["subdomain_key"] = subdomainKey,
                    ["machine_key"] = machineKey
                },
                reader => new ProfileAssignmentRow(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetInt32(reader.GetOrdinal("profile_index")),
                    reader.GetString(reader.GetOrdinal("device_type")),
                    reader.GetString(reader.GetOrdinal("browser")),
                    reader.GetString(reader.GetOrdinal("operating_system")),
                    reader.GetInt32(reader.GetOrdinal("hardware_age_years")),
                    reader.GetString(reader.GetOrdinal("headers_json")),
                    reader.GetInt32(reader.GetOrdinal("header_order_seed")),
                    reader.GetBoolean(reader.GetOrdinal("random_delay_enabled")),
                    reader.GetInt32(reader.GetOrdinal("random_delay_min_ms")),
                    reader.GetInt32(reader.GetOrdinal("random_delay_max_ms")),
                    reader.GetInt32(reader.GetOrdinal("requests_per_minute_per_subdomain"))),
                cancellationToken).ConfigureAwait(false);

            var row = rows.FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var headers = ReconProfileFactory.DeserializeHeaders(row.HeadersJson, row.HeaderOrderSeed, configuration.RandomizeHeaderOrderEnabled);
            return new ReconHeaderProfile(
                row.Id,
                request.TargetId,
                subdomainKey,
                machineKey,
                row.ProfileIndex,
                row.DeviceType,
                row.Browser,
                row.OperatingSystem,
                row.HardwareAgeYears,
                headers,
                row.RandomDelayEnabled,
                row.RandomDelayMinMs,
                row.RandomDelayMaxMs,
                row.RequestsPerMinutePerSubdomain,
                row.HeaderOrderSeed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve recon header profile for target {TargetId}, subdomain {SubdomainKey}, machine {MachineKey}.", request.TargetId, subdomainKey, machineKey);
            return null;
        }
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
                var config = JsonSerializer.Deserialize<ReconOrchestratorConfiguration>(json, JsonOptions);
                if (config is not null)
                {
                    return config;
                }
            }
            catch (JsonException)
            {
            }
        }

        return ReconOrchestratorConfiguration.FromOptions(options.Value);
    }

    private static int ResolveProfileIndex(
        ReconOrchestratorConfiguration configuration,
        Guid targetId,
        string subdomainKey,
        string machineKey)
    {
        var max = Math.Max(1, configuration.ReconProfilesPerSubdomain);
        var seed = ReconProfileFactory.StableInt($"{targetId:N}|{subdomainKey}|{machineKey}|profile-index");
        return Math.Abs(seed % max);
    }

    private static string NormalizeKey(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizeMachineKey(string value) => value.Trim().ToLowerInvariant();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProfileAssignmentRow(
        Guid Id,
        int ProfileIndex,
        string DeviceType,
        string Browser,
        string OperatingSystem,
        int HardwareAgeYears,
        string HeadersJson,
        int HeaderOrderSeed,
        bool RandomDelayEnabled,
        int RandomDelayMinMs,
        int RandomDelayMaxMs,
        int RequestsPerMinutePerSubdomain);
}
