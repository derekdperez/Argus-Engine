using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.Workers.HttpRequester;

internal static class ProxyRouting
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static ProxyServerConfiguration? SelectProxy(HttpRequestQueueSettings? settings, HttpRequestQueueItem item)
    {
        if (settings?.ProxyRoutingEnabled != true)
        {
            return null;
        }

        var proxies = ReadEnabledProxies(settings.ProxyServersJson).ToArray();
        if (proxies.Length == 0)
        {
            return null;
        }

        if (!settings.ProxyStickySubdomainsEnabled)
        {
            return proxies[Random.Shared.Next(proxies.Length)];
        }

        var assignmentKey = NormalizeAssignmentKey(item.DomainKey, item.RequestUrl);
        var salt = string.IsNullOrWhiteSpace(settings.ProxyAssignmentSalt)
            ? "argus-proxy-v1"
            : settings.ProxyAssignmentSalt.Trim();

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}|{assignmentKey}"), hash);

        var bucket = BitConverter.ToUInt32(hash[..4]);
        var index = (int)(bucket % (uint)proxies.Length);
        return proxies[index];
    }

    private static IEnumerable<ProxyServerConfiguration> ReadEnabledProxies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<ProxyServerConfiguration>>(json, JsonOptions) ?? [])
                .Where(proxy =>
                    proxy.Enabled &&
                    !string.IsNullOrWhiteSpace(proxy.Host) &&
                    proxy.Port is > 0 and <= 65535 &&
                    proxy.Scheme is "http" or "https");
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string NormalizeAssignmentKey(string? domainKey, string requestUrl)
    {
        if (!string.IsNullOrWhiteSpace(domainKey))
        {
            return domainKey.Trim().TrimEnd('.').ToLowerInvariant();
        }

        if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return uri.IdnHost.Trim().TrimEnd('.').ToLowerInvariant();
        }

        return requestUrl.Trim().ToLowerInvariant();
    }
}

internal sealed class ProxyServerConfiguration
{
    public string Id { get; set; } = string.Empty;

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

    public string CacheKey =>
        $"{Scheme}://{Username ?? string.Empty}:{Password ?? string.Empty}@{Host}:{Port}";
}
