using System.ComponentModel.DataAnnotations.Schema;

namespace ArgusEngine.Domain.Entities;

public sealed class HttpRequestQueueSettings
{
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public int GlobalRequestsPerMinute { get; set; } = 120_000;

    public int PerDomainRequestsPerMinute { get; set; } = 120;

    public int MaxConcurrency { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 30;

    [Column("rotate_user_agents")]
    public bool RotateUserAgents { get; set; }

    [Column("custom_user_agents_json")]
    public string? CustomUserAgentsJson { get; set; }

    [Column("randomize_header_order")]
    public bool RandomizeHeaderOrder { get; set; }

    [Column("use_random_jitter")]
    public bool UseRandomJitter { get; set; }

    [Column("min_jitter_ms")]
    public int MinJitterMs { get; set; }

    [Column("max_jitter_ms")]
    public int MaxJitterMs { get; set; } = 1000;

    [Column("spoof_referer")]
    public bool SpoofReferer { get; set; }

    [Column("custom_headers_json")]
    public string? CustomHeadersJson { get; set; }

    [Column("proxy_routing_enabled")]
    public bool ProxyRoutingEnabled { get; set; }

    [Column("proxy_sticky_subdomains_enabled")]
    public bool ProxyStickySubdomainsEnabled { get; set; } = true;

    [Column("proxy_assignment_salt")]
    public string? ProxyAssignmentSalt { get; set; } = "argus-proxy-v1";

    [Column("proxy_servers_json")]
    public string? ProxyServersJson { get; set; } = "[]";

    [Column("proxy_fingerprinting_enabled")]
    public bool ProxyFingerprintingEnabled { get; set; } = true;

    [Column("proxy_fingerprint_min_delay_ms")]
    public int ProxyFingerprintMinDelayMs { get; set; } = 150;

    [Column("proxy_fingerprint_max_delay_ms")]
    public int ProxyFingerprintMaxDelayMs { get; set; } = 1400;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
