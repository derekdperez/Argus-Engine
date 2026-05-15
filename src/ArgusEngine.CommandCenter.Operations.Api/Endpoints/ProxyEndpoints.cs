using System.Net.Sockets;
using System.Text.Json;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.Operations.Api.Endpoints;

public static class ProxyEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private const string PasswordPlaceholder = "********";

    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/proxy-routing").WithTags("Proxy Routing");

        group.MapGet(
                "/",
                async (ArgusDbContext db, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                    var settings = await GetOrCreateSettingsAsync(db, cancellationToken).ConfigureAwait(false);

                    return Results.Ok(ToOverview(settings));
                })
            .WithName("GetProxyRouting");

        group.MapPut(
                "/settings",
                async (ProxyRoutingSettingsDto input, ArgusDbContext db, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                    var settings = await GetOrCreateSettingsAsync(db, cancellationToken).ConfigureAwait(false);

                    settings.ProxyRoutingEnabled = input.ProxyRoutingEnabled;
                    settings.ProxyStickySubdomainsEnabled = input.ProxyStickySubdomainsEnabled;
                    settings.ProxyAssignmentSalt = string.IsNullOrWhiteSpace(input.ProxyAssignmentSalt)
                        ? "argus-proxy-v1"
                        : input.ProxyAssignmentSalt.Trim();
                    settings.ProxyFingerprintingEnabled = input.ProxyFingerprintingEnabled;
                    settings.ProxyFingerprintMinDelayMs = Math.Clamp(input.ProxyFingerprintMinDelayMs, 0, 60_000);
                    settings.ProxyFingerprintMaxDelayMs = Math.Clamp(
                        input.ProxyFingerprintMaxDelayMs,
                        settings.ProxyFingerprintMinDelayMs,
                        120_000);
                    settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return Results.Ok(ToOverview(settings));
                })
            .WithName("UpdateProxyRoutingSettings");

        group.MapPost(
                "/servers",
                async (ProxyServerDto input, ArgusDbContext db, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                    var settings = await GetOrCreateSettingsAsync(db, cancellationToken).ConfigureAwait(false);

                    var upsert = Normalize(input, existing: null, out var validationError);
                    if (validationError is not null)
                    {
                        return Results.BadRequest(new { error = validationError });
                    }

                    var servers = ReadServers(settings.ProxyServersJson).ToList();
                    servers.Add(upsert);
                    settings.ProxyServersJson = JsonSerializer.Serialize(servers, JsonOptions);
                    settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return Results.Ok(ToOverview(settings));
                })
            .WithName("CreateProxyServer");

        group.MapPut(
                "/servers/{id}",
                async (string id, ProxyServerDto input, ArgusDbContext db, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                    var settings = await GetOrCreateSettingsAsync(db, cancellationToken).ConfigureAwait(false);

                    var servers = ReadServers(settings.ProxyServersJson).ToList();
                    var existing = servers.FirstOrDefault(server => string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        return Results.NotFound();
                    }

                    input = input with { Id = id };
                    var updated = Normalize(input, existing, out var validationError);
                    if (validationError is not null)
                    {
                        return Results.BadRequest(new { error = validationError });
                    }

                    var index = servers.FindIndex(server => string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
                    servers[index] = updated;
                    settings.ProxyServersJson = JsonSerializer.Serialize(servers, JsonOptions);
                    settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return Results.Ok(ToOverview(settings));
                })
            .WithName("UpdateProxyServer");

        group.MapDelete(
                "/servers/{id}",
                async (string id, ArgusDbContext db, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                    var settings = await GetOrCreateSettingsAsync(db, cancellationToken).ConfigureAwait(false);

                    var servers = ReadServers(settings.ProxyServersJson).ToList();
                    var removed = servers.RemoveAll(server => string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (removed == 0)
                    {
                        return Results.NotFound();
                    }

                    settings.ProxyServersJson = JsonSerializer.Serialize(servers, JsonOptions);
                    settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return Results.NoContent();
                })
            .WithName("DeleteProxyServer");

        group.MapPost(
                "/servers/{id}/check",
                async (string id, ArgusDbContext db, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                    var settings = await GetOrCreateSettingsAsync(db, cancellationToken).ConfigureAwait(false);

                    var servers = ReadServers(settings.ProxyServersJson).ToList();
                    var server = servers.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (server is null)
                    {
                        return Results.NotFound();
                    }

                    var check = await CheckProxyTcpAsync(server.Host, server.Port, cancellationToken).ConfigureAwait(false);
                    server.LastCheckedAtUtc = DateTimeOffset.UtcNow;
                    server.LastStatus = check.Reachable ? "Reachable" : "Unreachable";
                    server.LastError = check.Error;

                    settings.ProxyServersJson = JsonSerializer.Serialize(servers, JsonOptions);
                    settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    return Results.Ok(ToOverview(settings));
                })
            .WithName("CheckProxyServer");

        group.MapGet(
                "/fingerprints",
                async (ArgusDbContext db, string? proxyId, string? target, int? take, CancellationToken cancellationToken) =>
                {
                    await EnsureProxyColumnsAsync(db, cancellationToken).ConfigureAwait(false);

                    var query = db.ProxyTargetFingerprintProfiles.AsNoTracking().AsQueryable();
                    if (!string.IsNullOrWhiteSpace(proxyId))
                    {
                        var proxyIdValue = proxyId.Trim();
                        query = query.Where(row => row.ProxyId == proxyIdValue);
                    }

                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var targetValue = target.Trim().ToLowerInvariant();
                        query = query.Where(row => row.TargetKey.Contains(targetValue));
                    }

                    var rows = await query
                        .OrderByDescending(row => row.LastUsedAtUtc ?? row.UpdatedAtUtc)
                        .Take(Math.Clamp(take ?? 200, 1, 2_000))
                        .Select(
                            row => new ProxyFingerprintAuditRowDto(
                                row.Id,
                                row.ProxyId,
                                row.ProxyName,
                                row.ProxyPublicIp,
                                row.TargetKey,
                                row.BrowserFamily,
                                row.BrowserVersion,
                                row.Platform,
                                row.AcceptLanguage,
                                row.ViewportWidth,
                                row.ViewportHeight,
                                row.UserAgent,
                                row.RefererTemplate,
                                row.DelayMinMs,
                                row.DelayMaxMs,
                                row.RequestCount,
                                row.CreatedAtUtc,
                                row.UpdatedAtUtc,
                                row.LastUsedAtUtc,
                                row.LastRequestUrl,
                                row.HeaderProfileJson))
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("ListProxyFingerprintAudits");

        return app;
    }

    private static async Task EnsureProxyColumnsAsync(ArgusDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE http_request_queue_settings
                ADD COLUMN IF NOT EXISTS proxy_routing_enabled boolean NOT NULL DEFAULT false,
                ADD COLUMN IF NOT EXISTS proxy_sticky_subdomains_enabled boolean NOT NULL DEFAULT true,
                ADD COLUMN IF NOT EXISTS proxy_assignment_salt text NULL DEFAULT 'argus-proxy-v1',
                ADD COLUMN IF NOT EXISTS proxy_servers_json text NULL DEFAULT '[]',
                ADD COLUMN IF NOT EXISTS proxy_fingerprinting_enabled boolean NOT NULL DEFAULT true,
                ADD COLUMN IF NOT EXISTS proxy_fingerprint_min_delay_ms integer NOT NULL DEFAULT 150,
                ADD COLUMN IF NOT EXISTS proxy_fingerprint_max_delay_ms integer NOT NULL DEFAULT 1400;

            CREATE TABLE IF NOT EXISTS proxy_target_fingerprint_profiles (
                id uuid NOT NULL PRIMARY KEY,
                proxy_id character varying(128) NOT NULL,
                proxy_name character varying(256) NOT NULL,
                proxy_public_ip character varying(64) NULL,
                target_key character varying(253) NOT NULL,
                browser_family character varying(64) NOT NULL,
                browser_version character varying(64) NOT NULL,
                platform character varying(64) NOT NULL,
                accept_language character varying(128) NOT NULL,
                viewport_width integer NOT NULL,
                viewport_height integer NOT NULL,
                user_agent character varying(512) NOT NULL,
                referer_template character varying(256) NOT NULL,
                header_profile_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                delay_min_ms integer NOT NULL DEFAULT 150,
                delay_max_ms integer NOT NULL DEFAULT 1400,
                request_count bigint NOT NULL DEFAULT 0,
                created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
                last_used_at_utc timestamp with time zone NULL,
                last_request_url character varying(4096) NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_proxy_target_fingerprint_profiles_proxy_target
                ON proxy_target_fingerprint_profiles (proxy_id, target_key);
            CREATE INDEX IF NOT EXISTS ix_proxy_target_fingerprint_profiles_last_used
                ON proxy_target_fingerprint_profiles (last_used_at_utc);
            """,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<HttpRequestQueueSettings> GetOrCreateSettingsAsync(
        ArgusDbContext db,
        CancellationToken cancellationToken)
    {
        var settings = await db.HttpRequestQueueSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (settings is not null)
        {
            return settings;
        }

        settings = new HttpRequestQueueSettings();
        db.HttpRequestQueueSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return settings;
    }

    private static ProxyRoutingOverviewDto ToOverview(HttpRequestQueueSettings settings)
    {
        var servers = ReadServers(settings.ProxyServersJson)
            .Select(ToDto)
            .OrderByDescending(server => server.Enabled)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProxyRoutingOverviewDto(
            new ProxyRoutingSettingsDto(
                settings.ProxyRoutingEnabled,
                settings.ProxyStickySubdomainsEnabled,
                settings.ProxyAssignmentSalt ?? "argus-proxy-v1",
                settings.ProxyFingerprintingEnabled,
                settings.ProxyFingerprintMinDelayMs,
                settings.ProxyFingerprintMaxDelayMs,
                settings.UpdatedAtUtc),
            servers,
            servers.Count(server => server.Enabled));
    }

    private static IReadOnlyList<ProxyServerConfiguration> ReadServers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProxyServerConfiguration>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ProxyServerConfiguration Normalize(
        ProxyServerDto input,
        ProxyServerConfiguration? existing,
        out string? validationError)
    {
        validationError = null;

        var scheme = string.IsNullOrWhiteSpace(input.Scheme)
            ? "http"
            : input.Scheme.Trim().ToLowerInvariant();

        if (scheme is not ("http" or "https"))
        {
            validationError = "Only http and https proxy schemes are supported by the HTTP requester worker.";
            return new ProxyServerConfiguration();
        }

        var host = input.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            validationError = "Proxy host is required.";
            return new ProxyServerConfiguration();
        }

        if (input.Port is <= 0 or > 65535)
        {
            validationError = "Proxy port must be between 1 and 65535.";
            return new ProxyServerConfiguration();
        }

        var suppliedPassword = input.Password?.Trim();
        var password = string.IsNullOrEmpty(suppliedPassword) || suppliedPassword == PasswordPlaceholder
            ? existing?.Password
            : suppliedPassword;

        var id = string.IsNullOrWhiteSpace(input.Id)
            ? Guid.NewGuid().ToString("n")
            : input.Id.Trim();

        return new ProxyServerConfiguration
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(input.Name) ? $"{host}:{input.Port}" : input.Name.Trim(),
            Scheme = scheme,
            Host = host,
            Port = input.Port,
            Username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim(),
            Password = password,
            Enabled = input.Enabled,
            PublicIpAddress = string.IsNullOrWhiteSpace(input.PublicIpAddress) ? null : input.PublicIpAddress.Trim(),
            Region = string.IsNullOrWhiteSpace(input.Region) ? null : input.Region.Trim(),
            Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
            LastCheckedAtUtc = existing?.LastCheckedAtUtc,
            LastStatus = existing?.LastStatus,
            LastError = existing?.LastError
        };
    }

    private static async Task<(bool Reachable, string? Error)> CheckProxyTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
        {
            return (false, "Host and port are required.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

            return (true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Connection timed out.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static ProxyServerDto ToDto(ProxyServerConfiguration server) =>
        new(
            server.Id,
            server.Name,
            server.Scheme,
            server.Host,
            server.Port,
            server.Username,
            string.IsNullOrWhiteSpace(server.Password) ? null : PasswordPlaceholder,
            server.Enabled,
            server.PublicIpAddress,
            server.Region,
            server.Notes,
            server.ProxyUri,
            server.LastCheckedAtUtc,
            server.LastStatus,
            server.LastError);
}

public sealed record ProxyRoutingOverviewDto(
    ProxyRoutingSettingsDto Settings,
    IReadOnlyList<ProxyServerDto> Servers,
    int EnabledServerCount);

public sealed record ProxyRoutingSettingsDto(
    bool ProxyRoutingEnabled,
    bool ProxyStickySubdomainsEnabled,
    string? ProxyAssignmentSalt,
    bool ProxyFingerprintingEnabled,
    int ProxyFingerprintMinDelayMs,
    int ProxyFingerprintMaxDelayMs,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProxyFingerprintAuditRowDto(
    Guid Id,
    string ProxyId,
    string ProxyName,
    string? ProxyPublicIp,
    string TargetKey,
    string BrowserFamily,
    string BrowserVersion,
    string Platform,
    string AcceptLanguage,
    int ViewportWidth,
    int ViewportHeight,
    string UserAgent,
    string RefererTemplate,
    int DelayMinMs,
    int DelayMaxMs,
    long RequestCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    string? LastRequestUrl,
    string HeaderProfileJson);

public sealed record ProxyServerDto(
    string? Id,
    string? Name,
    string? Scheme,
    string? Host,
    int Port,
    string? Username,
    string? Password,
    bool Enabled,
    string? PublicIpAddress,
    string? Region,
    string? Notes,
    string? ProxyUri,
    DateTimeOffset? LastCheckedAtUtc,
    string? LastStatus,
    string? LastError);

public sealed class ProxyServerConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = string.Empty;

    public string Scheme { get; set; } = "http";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool Enabled { get; set; } = true;

    public string? PublicIpAddress { get; set; }

    public string? Region { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? LastCheckedAtUtc { get; set; }

    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    public string ProxyUri => $"{Scheme}://{Host}:{Port}";
}
