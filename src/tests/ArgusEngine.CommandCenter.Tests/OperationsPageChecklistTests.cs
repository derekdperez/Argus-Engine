using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Messaging;
using Xunit;

namespace ArgusEngine.CommandCenter.Tests;

public sealed class EventMessageCompatibilityTests
{
    [Fact]
    public void Event_contracts_round_trip_through_outbox_message_keys()
    {
        var key = OutboxMessageTypeRegistry.GetMessageKey(typeof(TargetCreated));

        Assert.Equal("argus.events.target-created", key);
        Assert.True(OutboxMessageTypeRegistry.TryResolve(key, out var resolvedType));
        Assert.Same(typeof(TargetCreated), resolvedType);
    }

    [Theory]
    [InlineData("TargetCreated")]
    [InlineData("ArgusEngine.Contracts.Events.TargetCreated")]
    [InlineData("Nightmare.Contracts.Events.TargetCreated, Nightmare.Contracts")]
    public void Registry_resolves_supported_legacy_type_names(string legacyName)
    {
        Assert.True(OutboxMessageTypeRegistry.TryResolve(legacyName, out var resolvedType));
        Assert.Same(typeof(TargetCreated), resolvedType);
    }
}
