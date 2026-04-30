using System.Text;
using System.Text.RegularExpressions;

namespace NightmareV2.Application.TechnologyIdentification;

public static partial class TechnologyTagSlug
{
    public static string FromName(string name)
    {
        var normalized = name.Trim()
            .ToLowerInvariant()
            .Replace("+", "plus", StringComparison.Ordinal)
            .Replace("#", "sharp", StringComparison.Ordinal);

        normalized = NonAlphanumeric().Replace(normalized, "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "unknown";

        return $"technology:{normalized}";
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();
}
