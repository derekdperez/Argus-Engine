using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Messaging;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class OutboxMessageTypeRegistryTests
{
    [Theory]
    [InlineData(typeof(TargetCreated), "argus.events.target-created")]
    [InlineData(typeof(AssetDiscovered), "argus.events.asset-discovered")]
    public void GetMessageKey_GeneratesStableKebabCaseKeysForEventContracts(Type eventType, string expectedKey)
    {
        var key = OutboxMessageTypeRegistry.GetMessageKey(eventType);

        Assert.Equal(expectedKey, key);
        Assert.True(OutboxMessageTypeRegistry.TryResolve(key, out var resolvedType));
        Assert.Same(eventType, resolvedType);
    }

    [Theory]
    [InlineData("argus.events.target-created")]
    [InlineData("nightmare.events.target-created")]
    [InlineData("TargetCreated")]
    [InlineData("ArgusEngine.Contracts.Events.TargetCreated")]
    [InlineData("ArgusEngine.Contracts.Events.TargetCreated, ArgusEngine.Contracts")]
    [InlineData("Nightmare.Contracts.Events.TargetCreated, Nightmare.Contracts")]
    public void TryResolve_AcceptsCurrentAndLegacyEventIdentifiers(string identifier)
    {
        Assert.True(OutboxMessageTypeRegistry.TryResolve(identifier, out var resolvedType));
        Assert.Same(typeof(TargetCreated), resolvedType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("argus.events.not-real")]
    [InlineData("Nightmare.Contracts.Events.NotReal, Nightmare.Contracts")]
    public void TryResolve_ReturnsFalseForBlankOrUnknownIdentifiers(string identifier)
    {
        Assert.False(OutboxMessageTypeRegistry.TryResolve(identifier, out var resolvedType));
        Assert.Null(resolvedType);
    }

    [Fact]
    public void GetMessageKey_RejectsTypesThatAreNotEventContracts()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            OutboxMessageTypeRegistry.GetMessageKey(typeof(string)));

        Assert.Equal("messageType", exception.ParamName);
    }
}
