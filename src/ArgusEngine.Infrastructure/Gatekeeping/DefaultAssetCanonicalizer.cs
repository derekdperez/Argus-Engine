using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Infrastructure.Gatekeeping;

public sealed class DefaultAssetCanonicalizer : IAssetCanonicalizer
{
    private static readonly Regex IntegerSegment = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex GuidSegment = new(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    public CanonicalAsset Canonicalize(AssetDiscovered message)
    {
        return message.Kind switch
        {
            AssetKind.Target => new CanonicalAsset(AssetKind.Target, "target:" + message.RawValue.Trim().ToLowerInvariant(), message.RawValue.Trim().ToLowerInvariant()),
            AssetKind.Subdomain or AssetKind.Domain => CanonicalizeDomain(message.RawValue),
            AssetKind.Url or AssetKind.ApiEndpoint or AssetKind.JavaScriptFile or AssetKind.MarkdownBody => CanonicalizeUrl(message.Kind, message.RawValue),
            _ => new CanonicalAsset(message.Kind, message.Kind.ToString().ToLowerInvariant() + ":" + StableHash(message.RawValue), message.RawValue.Trim()),
        };
    }

    private static CanonicalAsset CanonicalizeDomain(string rawValue)
    {
        var host = rawValue.Trim().TrimEnd('.').ToLowerInvariant();
        return new CanonicalAsset(AssetKind.Subdomain, "host:" + host, host);
    }

    private static CanonicalAsset CanonicalizeUrl(AssetKind kind, string rawValue)
    {
        var url = rawValue.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (Uri.TryCreate("https://" + url, UriKind.Absolute, out uri))
                url = "https://" + url;
            else
                return new CanonicalAsset(kind, kind.ToString().ToLowerInvariant() + ":" + StableHash(url), url);
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = NormalizePath(uri.AbsolutePath);
        var query = NormalizeQuery(uri.Query);

        var canonicalKey = $"url:{scheme}://{host}{port}{path}{query}";
        var normalizedDisplay = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

        return new CanonicalAsset(kind, canonicalKey, normalizedDisplay);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return "/";

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(
                segment =>
                {
                    var decoded = Uri.UnescapeDataString(segment);
                    if (GuidSegment.IsMatch(decoded))
                        return "{guid}";
                    if (IntegerSegment.IsMatch(decoded))
                        return "{id}";
                    return Uri.EscapeDataString(decoded.ToLowerInvariant());
                });

        var normalized = "/" + string.Join("/", segments);
        return path.EndsWith("/", StringComparison.Ordinal) ? normalized + "/" : normalized;
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

    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }
}
