using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NightmareV2.Application.Gatekeeping;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Infrastructure.Gatekeeping;

public sealed class DefaultAssetCanonicalizer : IAssetCanonicalizer
{
    private static readonly Regex IntegerSegment = new(@"^\d+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex UuidSegment = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public CanonicalAsset Canonicalize(AssetDiscovered message)
    {
        var raw = message.RawValue.Trim();
        return message.Kind switch
        {
            AssetKind.Target => CanonicalizeTarget(raw),
            AssetKind.Url or AssetKind.JavaScriptFile or AssetKind.MarkdownBody => CanonicalizeUrl(raw, message.Kind),
            AssetKind.ApiEndpoint => CanonicalizeApiEndpoint(raw),
            AssetKind.ApiMethod => CanonicalizeApiMethod(raw),
            AssetKind.Parameter => CanonicalizeParameter(raw),
            AssetKind.Subdomain or AssetKind.Domain => CanonicalizeHost(raw, message.Kind),
            AssetKind.IpAddress => CanonicalizeIpAddress(raw),
            AssetKind.CidrBlock => CanonicalizeCidr(raw),
            AssetKind.Asn => CanonicalizeAsn(raw),
            AssetKind.OpenPort => CanonicalizeOpenPort(raw),
            AssetKind.TlsCertificate => CanonicalizeHashBacked(raw, "tls_cert"),
            AssetKind.Secret => CanonicalizeSecret(raw),
            AssetKind.CloudBucket => CanonicalizeCloudBucket(raw),
            AssetKind.Email => new CanonicalAsset(AssetKind.Email, $"email:{raw.ToLowerInvariant()}", raw.ToLowerInvariant()),
            _ => new CanonicalAsset(message.Kind, $"{message.Kind.ToString().ToLowerInvariant()}:{StableHash(raw)}", raw),
        };
    }

    private static CanonicalAsset CanonicalizeTarget(string rootDomain)
    {
        var root = NormalizeHost(rootDomain);
        return new CanonicalAsset(AssetKind.Target, $"target:{root}", root);
    }

    private static CanonicalAsset CanonicalizeHost(string host, AssetKind kind)
    {
        var h = NormalizeHost(host);
        return new CanonicalAsset(kind, $"host:{h}", h);
    }

    private static string NormalizeHost(string host)
    {
        var h = host.Trim().TrimEnd('/').TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(h, UriKind.Absolute, out var uri))
                h = uri.IdnHost;
        }

        var idn = new IdnMapping();
        try
        {
            h = idn.GetAscii(h);
        }
        catch (ArgumentException)
        {
            // keep unicode form if punycode fails
        }

        return h.TrimEnd('.').ToLowerInvariant();
    }

    private static CanonicalAsset CanonicalizeIpAddress(string raw)
    {
        if (IPAddress.TryParse(raw, out var ip))
            return new CanonicalAsset(AssetKind.IpAddress, $"ip:{ip}", ip.ToString());

        return new CanonicalAsset(AssetKind.IpAddress, $"ip:{raw.ToLowerInvariant()}", raw);
    }

    private static CanonicalAsset CanonicalizeCidr(string raw)
    {
        var normalized = raw.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return new CanonicalAsset(AssetKind.CidrBlock, $"cidr:{normalized}", normalized);
    }

    private static CanonicalAsset CanonicalizeAsn(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        var normalized = string.IsNullOrWhiteSpace(digits) ? raw.Trim().ToLowerInvariant() : digits;
        return new CanonicalAsset(AssetKind.Asn, $"asn:{normalized}", normalized);
    }

    private static CanonicalAsset CanonicalizeUrl(string raw, AssetKind kind)
    {
        if (!TryCreateHttpUri(raw, out var uri))
            return new CanonicalAsset(kind, $"url:{StableHash(raw)}", raw);

        var normalized = NormalizeHttpUri(uri, templatePath: false);
        return new CanonicalAsset(kind, $"url:{normalized}", normalized);
    }

    private static CanonicalAsset CanonicalizeApiEndpoint(string raw)
    {
        if (!TryCreateHttpUri(raw, out var uri))
            return new CanonicalAsset(AssetKind.ApiEndpoint, $"api_endpoint:{StableHash(raw)}", raw);

        var normalized = NormalizeHttpUri(uri, templatePath: true, includeQuery: false);
        return new CanonicalAsset(AssetKind.ApiEndpoint, $"api_endpoint:{normalized}", normalized);
    }

    private static CanonicalAsset CanonicalizeApiMethod(string raw)
    {
        var trimmed = raw.Trim();
        var method = "GET";
        var endpointText = trimmed;

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && LooksLikeHttpMethod(parts[0]))
        {
            method = parts[0].ToUpperInvariant();
            endpointText = parts[1];
        }
        else if (trimmed.Contains('|', StringComparison.Ordinal))
        {
            var pipe = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
            if (LooksLikeHttpMethod(pipe[0]))
            {
                method = pipe[0].ToUpperInvariant();
                endpointText = pipe[1];
            }
        }

        var endpoint = CanonicalizeApiEndpoint(endpointText);
        return new CanonicalAsset(
            AssetKind.ApiMethod,
            $"api_method:{endpoint.CanonicalKey}:{method}",
            $"{method} {endpoint.NormalizedDisplay}");
    }

    private static CanonicalAsset CanonicalizeParameter(string raw)
    {
        // Preferred raw format:
        // api_method:<endpoint-key>:<METHOD>|<location>|<name>
        // or url:<url-key>|<location>|<name>
        var parts = raw.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
        {
            var owner = parts[0];
            var location = parts[1].ToLowerInvariant();
            var name = parts[2].ToLowerInvariant();
            var prefix = owner.StartsWith("api_method:", StringComparison.OrdinalIgnoreCase)
                ? "api_parameter"
                : "url_parameter";
            return new CanonicalAsset(AssetKind.Parameter, $"{prefix}:{owner}:{location}:{name}", name);
        }

        return new CanonicalAsset(AssetKind.Parameter, $"parameter:{StableHash(raw)}", raw);
    }

    private static CanonicalAsset CanonicalizeOpenPort(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        return new CanonicalAsset(AssetKind.OpenPort, $"open_port:{normalized}", normalized);
    }

    private static CanonicalAsset CanonicalizeHashBacked(string raw, string prefix)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        return new CanonicalAsset(AssetKind.TlsCertificate, $"{prefix}:{normalized}", normalized);
    }

    private static CanonicalAsset CanonicalizeSecret(string raw)
    {
        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        var type = parts.Length == 2 ? parts[0].ToLowerInvariant() : "unknown";
        var value = parts.Length == 2 ? parts[1] : raw;
        return new CanonicalAsset(AssetKind.Secret, $"secret:{type}:{StableHash(value)}", raw);
    }

    private static CanonicalAsset CanonicalizeCloudBucket(string raw)
    {
        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        var provider = parts.Length == 2 ? parts[0].ToLowerInvariant() : "unknown";
        var bucket = (parts.Length == 2 ? parts[1] : raw).Trim().ToLowerInvariant();
        return new CanonicalAsset(AssetKind.CloudBucket, $"cloud_bucket:{provider}:{bucket}", bucket);
    }

    private static bool TryCreateHttpUri(string raw, out Uri uri)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out uri!))
        {
            if (!Uri.TryCreate("https://" + raw.TrimStart('/'), UriKind.Absolute, out uri!))
                return false;
        }

        return uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static string NormalizeHttpUri(Uri uri, bool templatePath, bool includeQuery = true)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        path = NormalizePath(path, templatePath);
        var query = includeQuery ? NormalizeQuery(uri.Query) : "";
        return $"{scheme}://{host}{port}{path}{query}";
    }

    private static string NormalizePath(string path, bool templatePath)
    {
        if (!templatePath)
            return path;

        var segments = path.Split('/', StringSplitOptions.None)
            .Select(
                segment =>
                {
                    var decoded = Uri.UnescapeDataString(segment);
                    if (decoded.StartsWith("{", StringComparison.Ordinal) && decoded.EndsWith("}", StringComparison.Ordinal))
                        return decoded;
                    if (UuidSegment.IsMatch(decoded))
                        return "{uuid}";
                    if (IntegerSegment.IsMatch(decoded))
                        return "{id}";
                    return segment;
                });

        return string.Join('/', segments);
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query == "?")
            return "";

        var trimmed = query.TrimStart('?');
        var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(
                p =>
                {
                    var kv = p.Split('=', 2);
                    var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
                    var value = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
                    return new KeyValuePair<string, string>(key, value);
                })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => string.IsNullOrEmpty(p.Value) ? Uri.EscapeDataString(p.Key) : $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")
            .ToArray();

        return parts.Length == 0 ? "" : "?" + string.Join('&', parts);
    }

    private static bool LooksLikeHttpMethod(string value) =>
        value.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || value.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || value.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
        || value.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || value.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || value.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }
}
