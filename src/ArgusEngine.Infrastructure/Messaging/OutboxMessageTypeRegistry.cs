using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Infrastructure.Messaging;

internal static class OutboxMessageTypeRegistry
{
    private static readonly IReadOnlyDictionary<string, Type> TypesByKey =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["asset.discovered.v1"] = typeof(AssetDiscovered),
            ["asset.relationship.discovered.v1"] = typeof(AssetRelationshipDiscovered),
            ["critical.high.value.finding.alert.v1"] = typeof(CriticalHighValueFindingAlert),
            ["port.scan.requested.v1"] = typeof(PortScanRequested),
            ["scannable.content.available.v1"] = typeof(ScannableContentAvailable),
            ["subdomain.enumeration.requested.v1"] = typeof(SubdomainEnumerationRequested),
            ["target.created.v1"] = typeof(TargetCreated),
        };

    private static readonly IReadOnlyDictionary<Type, string> KeysByType =
        TypesByKey.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static string GetMessageKey(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        if (KeysByType.TryGetValue(messageType, out var key))
        {
            return key;
        }

        throw new InvalidOperationException(
            $"Event type '{messageType.FullName}' is not registered in {nameof(OutboxMessageTypeRegistry)}.");
    }

    public static bool TryResolve(string messageKey, out Type? messageType)
    {
        if (string.IsNullOrWhiteSpace(messageKey))
        {
            messageType = null;
            return false;
        }

        if (TypesByKey.TryGetValue(messageKey, out var resolvedType))
        {
            messageType = resolvedType;
            return true;
        }

        messageType = null;
        return false;
    }
}
