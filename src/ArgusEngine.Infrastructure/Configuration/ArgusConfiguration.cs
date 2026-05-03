using Microsoft.Extensions.Configuration;

namespace ArgusEngine.Infrastructure.Configuration;

public static class ArgusConfiguration
{
    public static IConfigurationSection GetArgusSection(this IConfiguration configuration, string childSection)
    {
        var normalized = NormalizeKey(childSection);
        var argusKey = string.IsNullOrEmpty(normalized) ? "Argus" : $"Argus:{normalized}";

        return configuration.GetSection(argusKey);
    }

    public static string? GetArgusValue(this IConfiguration configuration, string key)
    {
        var normalized = NormalizeKey(key);
        var envKey = ToScreamingSnake(normalized);

        return configuration[$"Argus:{normalized}"]
               ?? configuration[$"ARGUS_{envKey}"]
               ?? Environment.GetEnvironmentVariable($"ARGUS_{envKey}");
    }

    public static T GetArgusValue<T>(this IConfiguration configuration, string key, T defaultValue)
    {
        var value = configuration.GetArgusValue(key);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var argusPath = $"Argus:{NormalizeKey(key)}";

        if (!string.IsNullOrWhiteSpace(configuration[argusPath]))
            return configuration.GetValue(argusPath, defaultValue)!;

        try
        {
            if (typeof(T) == typeof(bool))
            {
                var boolValue = string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
                return (T)(object)boolValue;
            }

            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string NormalizeKey(string key) =>
        (key ?? string.Empty).Trim().Trim(':');

    private static string ToScreamingSnake(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var chars = new List<char>(key.Length + 8);
        char previous = '\0';

        foreach (var c in key)
        {
            if (c == ':' || c == '-' || c == '.')
            {
                chars.Add('_');
                previous = '_';
                continue;
            }

            if (char.IsUpper(c) && previous != '\0' && previous != '_' && !char.IsUpper(previous))
                chars.Add('_');

            chars.Add(char.ToUpperInvariant(c));
            previous = c;
        }

        return new string(chars.ToArray()).Replace("__", "_", StringComparison.Ordinal);
    }
}
