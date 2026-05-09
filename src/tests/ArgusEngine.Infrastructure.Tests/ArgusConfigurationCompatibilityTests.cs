using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Messaging;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class ArgusConfigurationCompatibilityTests
{
    [Fact]
    public void TryResolve_MapsLegacyNightmareNamespaceToArgusContracts()
    {
        const string legacyTypeName = "Nightmare.Contracts.Events.AssetDiscovered, Nightmare.Contracts";

        Assert.True(OutboxMessageTypeRegistry.TryResolve(legacyTypeName, out var resolvedType));
        Assert.Same(typeof(AssetDiscovered), resolvedType);
    }

    [Fact]
    public void TryResolve_RejectsUnknownNightmareContractsInsteadOfGuessing()
    {
        const string unknownLegacyTypeName = "Nightmare.Contracts.Events.UnknownEvent, Nightmare.Contracts";

        Assert.False(OutboxMessageTypeRegistry.TryResolve(unknownLegacyTypeName, out var resolvedType));
        Assert.Null(resolvedType);
    }
}
