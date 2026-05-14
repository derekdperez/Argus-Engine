using System.Text;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Infrastructure.Messaging;

public static class OutboxMessageTypeRegistry
{
    private const string MessageKeyPrefix = "argus.events.";

    private static readonly Type[] KnownEventTypes = typeof(IEventEnvelope)
        .Assembly
        .GetTypes()
        .Where(type =>
            type is { IsClass: true, IsAbstract: false } &&
            typeof(IEventEnvelope).IsAssignableFrom(type))
        .OrderBy(type => type.FullName, StringComparer.Ordinal)
        .ToArray();

    private static readonly Dictionary<string, Type> TypesByMessageKey = BuildMessageKeyMap();

    private static readonly Dictionary<string, Type> LegacyTypesByName = BuildLegacyTypeNameMap();

    public static string GetMessageKey(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (!typeof(IEventEnvelope).IsAssignableFrom(messageType))
        {
            throw new ArgumentException(
                $"Message type '{messageType.FullName}' does not implement {nameof(IEventEnvelope)}.",
                nameof(messageType));
        }

        var contractType = KnownEventTypes.FirstOrDefault(type => type == messageType);
        if (contractType is null)
        {
            throw new InvalidOperationException(
                $"Message type '{messageType.FullName}' is not a registered Argus event contract. " +
                $"Add the contract to {typeof(IEventEnvelope).Assembly.GetName().Name} before enqueueing it.");
        }

        return BuildMessageKey(contractType);
    }

    public static bool TryResolve(string messageKeyOrLegacyTypeName, out Type? messageType)
    {
        if (string.IsNullOrWhiteSpace(messageKeyOrLegacyTypeName))
        {
            messageType = null;
            return false;
        }

        // 1. Direct match (current keys)
        if (TypesByMessageKey.TryGetValue(messageKeyOrLegacyTypeName, out messageType))
        {
            return true;
        }

        // 2. Legacy prefix mapping (nightmare -> argus)
        if (messageKeyOrLegacyTypeName.StartsWith("nightmare.events.", StringComparison.OrdinalIgnoreCase))
        {
            var mappedKey = "argus.events." + messageKeyOrLegacyTypeName["nightmare.events.".Length..];
            if (TypesByMessageKey.TryGetValue(mappedKey, out messageType))
            {
                return true;
            }
        }

        // 3. Exact match for legacy names (AssemblyQualifiedName, FullName, Name)
        if (LegacyTypesByName.TryGetValue(messageKeyOrLegacyTypeName, out messageType))
        {
            return true;
        }

        if (TryResolveByTypeNamePrefix(messageKeyOrLegacyTypeName, out messageType))
        {
            return true;
        }

        // 4. Fallback: try mapping Nightmare namespace to ArgusEngine in the type string
        if (messageKeyOrLegacyTypeName.Contains("Nightmare", StringComparison.OrdinalIgnoreCase))
        {
            var mappedName = messageKeyOrLegacyTypeName
                .Replace("Nightmare.Contracts", "ArgusEngine.Contracts", StringComparison.OrdinalIgnoreCase)
                .Replace("Nightmare.Events", "ArgusEngine.Contracts.Events", StringComparison.OrdinalIgnoreCase);

            if (LegacyTypesByName.TryGetValue(mappedName, out messageType))
            {
                return true;
            }

            if (TryResolveByTypeNamePrefix(mappedName, out messageType))
            {
                return true;
            }
        }

        messageType = null;
        return false;
    }

    private static Dictionary<string, Type> BuildMessageKeyMap()
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var eventType in KnownEventTypes)
        {
            var key = BuildMessageKey(eventType);
            if (!map.TryAdd(key, eventType))
            {
                throw new InvalidOperationException(
                    $"Duplicate outbox message key '{key}' for '{map[key].FullName}' and '{eventType.FullName}'.");
            }
        }

        return map;
    }

    private static Dictionary<string, Type> BuildLegacyTypeNameMap()
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var eventType in KnownEventTypes)
        {
            AddIfPresent(map, eventType.AssemblyQualifiedName, eventType);
            AddIfPresent(map, eventType.FullName, eventType);
            AddIfPresent(map, eventType.Name, eventType);
        }

        return map;
    }

    private static void AddIfPresent(Dictionary<string, Type> map, string? key, Type eventType)
    {
        if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
        {
            map.Add(key, eventType);
        }
    }

    private static string BuildMessageKey(Type messageType) => MessageKeyPrefix + ToKebabCase(messageType.Name);

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];

            if (current is '_' or ' ' or '.')
            {
                AppendDashIfNeeded(builder);
                continue;
            }

            if (char.IsUpper(current))
            {
                var previous = i > 0 ? value[i - 1] : '\0';
                var next = i + 1 < value.Length ? value[i + 1] : '\0';
                var startsNewWord =
                    i > 0 &&
                    builder.Length > 0 &&
                    builder[^1] != '-' &&
                    (char.IsLower(previous) || char.IsDigit(previous) || char.IsLower(next));

                if (startsNewWord)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static void AppendDashIfNeeded(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }

    private static bool TryResolveByTypeNamePrefix(string messageIdentifier, out Type? messageType)
    {
        var commaIndex = messageIdentifier.IndexOf(',');
        if (commaIndex <= 0)
        {
            messageType = null;
            return false;
        }

        var typeName = messageIdentifier[..commaIndex].Trim();
        return LegacyTypesByName.TryGetValue(typeName, out messageType);
    }
}
