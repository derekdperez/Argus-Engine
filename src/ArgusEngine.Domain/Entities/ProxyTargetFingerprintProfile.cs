using System.ComponentModel.DataAnnotations.Schema;

namespace ArgusEngine.Domain.Entities;

public sealed class ProxyTargetFingerprintProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("proxy_id")]
    public string ProxyId { get; set; } = string.Empty;

    [Column("proxy_name")]
    public string ProxyName { get; set; } = string.Empty;

    [Column("proxy_public_ip")]
    public string? ProxyPublicIp { get; set; }

    [Column("target_key")]
    public string TargetKey { get; set; } = string.Empty;

    [Column("browser_family")]
    public string BrowserFamily { get; set; } = "Chrome";

    [Column("browser_version")]
    public string BrowserVersion { get; set; } = "136";

    [Column("platform")]
    public string Platform { get; set; } = "Windows";

    [Column("accept_language")]
    public string AcceptLanguage { get; set; } = "en-US,en;q=0.9";

    [Column("viewport_width")]
    public int ViewportWidth { get; set; } = 1920;

    [Column("viewport_height")]
    public int ViewportHeight { get; set; } = 1080;

    [Column("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    [Column("referer_template")]
    public string RefererTemplate { get; set; } = "direct";

    [Column("header_profile_json")]
    public string HeaderProfileJson { get; set; } = "{}";

    [Column("delay_min_ms")]
    public int DelayMinMs { get; set; } = 150;

    [Column("delay_max_ms")]
    public int DelayMaxMs { get; set; } = 1400;

    [Column("request_count")]
    public long RequestCount { get; set; }

    [Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_used_at_utc")]
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    [Column("last_request_url")]
    public string? LastRequestUrl { get; set; }
}
