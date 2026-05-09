using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Messaging;
using Xunit;

namespace ArgusEngine.IntegrationTests.Infrastructure.Persistence;

public sealed class DatabaseIntegrationTests
{
    [Fact]
    public void OutboxMessageKeysAreStableForPersistedBusJournalRows()
    {
        var targetCreatedKey = OutboxMessageTypeRegistry.GetMessageKey(typeof(TargetCreated));
        var assetDiscoveredKey = OutboxMessageTypeRegistry.GetMessageKey(typeof(AssetDiscovered));

        Assert.Equal("argus.events.target-created", targetCreatedKey);
        Assert.Equal("argus.events.asset-discovered", assetDiscoveredKey);
    }

    [Fact]
    public void PersistedLegacyMessageIdentifiersCanBeResolvedDuringReplay()
    {
        var persistedIdentifiers = new[]
        {
            "argus.events.target-created",
            "nightmare.events.target-created",
            "TargetCreated",
            "Nightmare.Contracts.Events.TargetCreated, Nightmare.Contracts"
        };

        foreach (var identifier in persistedIdentifiers)
        {
            Assert.True(
                OutboxMessageTypeRegistry.TryResolve(identifier, out var resolvedType),
                $"Expected '{identifier}' to resolve.");

            Assert.Same(typeof(TargetCreated), resolvedType);
        }
    }
}
