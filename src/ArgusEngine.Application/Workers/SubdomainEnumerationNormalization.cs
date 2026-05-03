using System.Text.RegularExpressions;

namespace ArgusEngine.Application.Workers;

public static partial class SubdomainEnumerationNormalization
{
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DnsLabelRegex();

    public static string? NormalizeHostname(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var value = input.Trim();
        if (value.Contains("://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (value.Contains('/', StringComparison.Ordinal))
            return null;
        if (value.Contains('*', StringComparison.Ordinal))
            return null;
        if (value.Contains(' ', StringComparison.Ordinal))
            return null;

        value = value.TrimEnd('.').ToLowerInvariant();
        if (value.Length is 0 or > 253 || value.Contains("..", StringComparison.Ordinal))
            return null;

        return value;
    }

    public static bool IsValidHostname(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return false;
        if (hostname.Length > 253)
            return false;

        var labels = hostname.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length < 2)
            return false;

        foreach (var label in labels)
        {
            if (!DnsLabelRegex().IsMatch(label))
                return false;
        }

        return true;
    }

    public static bool IsInScope(string hostname, string rootDomain)
    {
        var root = NormalizeHostname(rootDomain);
        if (root is null)
            return false;

        return hostname.Equals(root, StringComparison.OrdinalIgnoreCase)
               || hostname.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
    }

    public static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "value";

        var chars = value.Select(
                c =>
                {
                    if (char.IsLetterOrDigit(c))
                        return c;
                    return c switch
                    {
                        '-' or '_' or '.' => c,
                        _ => '_',
                    };
                })
            .ToArray();
        var safe = new string(chars).Trim('_', '.');
        return string.IsNullOrWhiteSpace(safe) ? "value" : safe;
    }
}
