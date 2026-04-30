using System.Text.RegularExpressions;

namespace NightmareV2.Workers.Spider;

internal static class ApiRouteInference
{
    private static readonly Regex IntegerSegment = new(@"^\d+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex UuidSegment = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static bool TryInferEndpoint(Uri uri, out string endpointUrl)
    {
        endpointUrl = "";
        if (!LooksApiLike(uri.AbsolutePath))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.None)
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

        var path = string.Join('/', segments);
        endpointUrl = $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{path}";
        return true;
    }

    public static IReadOnlyList<string> QueryParameterNames(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Query) || uri.Query == "?")
            return Array.Empty<string>();

        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)[0])
            .Select(Uri.UnescapeDataString)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksApiLike(string path) =>
        path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(path, @"^/v\d+/", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
        || path.Contains("/graphql", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/rest/", StringComparison.OrdinalIgnoreCase);
}
