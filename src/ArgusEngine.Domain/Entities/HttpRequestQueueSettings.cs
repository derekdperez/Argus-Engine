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

    [Column("proxy_routing_enabled")]
    public bool ProxyRoutingEnabled { get; set; }

    [Column("proxy_sticky_subdomains_enabled")]
    public bool ProxyStickySubdomainsEnabled { get; set; } = true;

    [Column("proxy_assignment_salt")]
    public string? ProxyAssignmentSalt { get; set; } = "argus-proxy-v1";

    [Column("proxy_servers_json")]
    public string? ProxyServersJson { get; set; } = "[]";

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
