using System.Globalization;
using ArgusEngine.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;

namespace ArgusEngine.Infrastructure.Configuration;

public static class ArgusConfiguration
{
    private const string CurrentSectionName = "Argus";
    private const string LegacySectionName = "Nightmare";
    private const string CurrentEnvironmentPrefix = "ARGUS";
    private const string LegacyEnvironmentPrefix = "NIGHTMARE";

    public static IConfigurationSection GetArgusSection(this IConfiguration configuration)
    {
        var current = configuration.GetSection(CurrentSectionName);

        if (current.Exists())
        {
            return current;
        }

        var legacy = configuration.GetSection(LegacySectionName);

        if (legacy.Exists())
        {
            RecordConfigAliasAccess("Nightmare", LegacySectionName);
            return legacy;
        }

        return current;
    }

    public static IConfigurationSection GetArgusSection(this IConfiguration configuration, string key)
    {
        var normalized = NormalizeKey(key);
        var current = configuration.GetSection($"{CurrentSectionName}:{normalized}");

        if (current.Exists())
        {
            return current;
        }

        var legacy = configuration.GetSection($"{LegacySectionName}:{normalized}");

        if (legacy.Exists())
        {
            RecordConfigAliasAccess("Nightmare", normalized);
            return legacy;
        }

        return current;
    }

    public static string? GetArgusValue(this IConfiguration configuration, string key)
    {
        var normalized = NormalizeKey(key);

        var currentPath = $"{CurrentSectionName}:{normalized}";
        var current = configuration[currentPath];

        if (current is not null)
        {
            RecordConfigAliasAccess("Argus", normalized);
            return current;
        }

        var legacyPath = $"{LegacySectionName}:{normalized}";
        var legacy = configuration[legacyPath];

        if (legacy is not null)
        {
            RecordConfigAliasAccess("Nightmare", normalized);
            return legacy;
        }

        var currentEnvironmentKey = ToScreamingSnake(CurrentEnvironmentPrefix, normalized);
        var currentEnvironmentValue = configuration[currentEnvironmentKey] ?? Environment.GetEnvironmentVariable(currentEnvironmentKey);

        if (currentEnvironmentValue is not null)
        {
            RecordConfigAliasAccess(CurrentEnvironmentPrefix, normalized);
            return currentEnvironmentValue;
        }

        var legacyEnvironmentKey = ToScreamingSnake(LegacyEnvironmentPrefix, normalized);
        var legacyEnvironmentValue = configuration[legacyEnvironmentKey] ?? Environment.GetEnvironmentVariable(legacyEnvironmentKey);

        if (legacyEnvironmentValue is not null)
        {
            RecordConfigAliasAccess(LegacyEnvironmentPrefix, normalized);
            return legacyEnvironmentValue;
        }

        return null;
    }

    public static T GetArgusValue<T>(this IConfiguration configuration, string key, T defaultValue)
    {
        var value = configuration.GetArgusValue(key);

        if (value is null)
        {
            return defaultValue;
        }

        return ConvertValue(value, defaultValue);
    }

    public static IReadOnlyList<string> GetArgusCompatibilityKeys(string key)
    {
        var normalized = NormalizeKey(key);

        return
        [
            $"{CurrentSectionName}:{normalized}",
            $"{LegacySectionName}:{normalized}",
            ToScreamingSnake(CurrentEnvironmentPrefix, normalized),
            ToScreamingSnake(LegacyEnvironmentPrefix, normalized)
        ];
    }

    private static T ConvertValue<T>(string value, T defaultValue)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        try
        {
            if (targetType == typeof(string))
            {
                return (T)(object)value;
            }

            if (targetType == typeof(bool))
            {
                if (bool.TryParse(value, out var parsedBool))
                {
                    return (T)(object)parsedBool;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIntBool))
                {
                    return (T)(object)(parsedIntBool != 0);
                }

                return defaultValue;
            }

            if (targetType.IsEnum)
            {
                return (T)Enum.Parse(targetType, value, ignoreCase: true);
            }

            return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static string NormalizeKey(string key)
    {
        return key.Trim().Replace("__", ":", StringComparison.Ordinal);
    }

    private static string ToScreamingSnake(string prefix, string key)
    {
        var normalized = NormalizeKey(key);
        var chars = new List<char>(prefix.Length + normalized.Length + 1);
        chars.AddRange(prefix);
        chars.Add('_');

        var previousWasSeparator = true;

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (char.IsUpper(character) && chars.Count > prefix.Length + 1 && !previousWasSeparator)
                {
                    var previous = chars[^1];

                    if (char.IsLower(previous) || char.IsDigit(previous))
                    {
                        chars.Add('_');
                    }
                }

                chars.Add(char.ToUpperInvariant(character));
                previousWasSeparator = false;
            }
            else
            {
                if (!previousWasSeparator)
                {
                    chars.Add('_');
                    previousWasSeparator = true;
                }
            }
        }

        while (chars.Count > prefix.Length + 1 && chars[^1] == '_')
        {
            chars.RemoveAt(chars.Count - 1);
        }

        return new string(chars.ToArray());
    }

    private static void RecordConfigAliasAccess(string alias, string key)
    {
        try
        {
            ArgusMeters.ConfigAliasAccesses.Add(
                1,
                new KeyValuePair<string, object?>("alias", alias),
                new KeyValuePair<string, object?>("key", key));
        }
        catch
        {
            // Configuration helpers must never fail because a metrics listener is unavailable.
        }
    }
}
