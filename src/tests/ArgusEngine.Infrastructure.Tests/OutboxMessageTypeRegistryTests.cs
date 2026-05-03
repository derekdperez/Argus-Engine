using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Messaging;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class OutboxMessageTypeRegistryTests
{
    [Fact]
    public void GetMessageKey_ReturnsResolvableStableKey()
    {
        var key = OutboxMessageTypeRegistry.GetMessageKey(typeof(TargetCreated));

        Assert.Equal("argus.events.target-created", key);
        Assert.True(OutboxMessageTypeRegistry.TryResolve(key, out var resolvedType));
        Assert.Equal(typeof(TargetCreated), resolvedType);
    }

    [Fact]
    public void TryResolve_ResolvesLegacyAssemblyQualifiedTypeName()
    {
        var legacyTypeName = typeof(TargetCreated).AssemblyQualifiedName!;

        Assert.True(OutboxMessageTypeRegistry.TryResolve(legacyTypeName, out var resolvedType));
        Assert.Equal(typeof(TargetCreated), resolvedType);
    }

    [Fact]
    public void TryResolve_RejectsUnknownMessageKey()
    {
        Assert.False(OutboxMessageTypeRegistry.TryResolve("argus.events.not-a-real-event", out var resolvedType));
        Assert.Null(resolvedType);
    }
}
