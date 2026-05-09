using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Messaging;
using Xunit;

namespace ArgusEngine.RouteCompatibilityTests;

public sealed class CommandCenterRouteSnapshotTests
{
    [Theory]
    [InlineData("argus.events.target-created")]
    [InlineData("nightmare.events.target-created")]
    public void CurrentAndLegacyTargetCreatedMessageKeysResolveToTheSameContract(string identifier)
    {
        Assert.True(OutboxMessageTypeRegistry.TryResolve(identifier, out var resolvedType));
        Assert.Same(typeof(TargetCreated), resolvedType);
    }

    [Fact]
    public void AllRegisteredEventContractsHaveResolvableMessageKeys()
    {
        var eventTypes = typeof(IEventEnvelope).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           typeof(IEventEnvelope).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(eventTypes);

        foreach (var eventType in eventTypes)
        {
            var key = OutboxMessageTypeRegistry.GetMessageKey(eventType);

            Assert.StartsWith("argus.events.", key, StringComparison.Ordinal);
            Assert.True(OutboxMessageTypeRegistry.TryResolve(key, out var resolvedType));
            Assert.Same(eventType, resolvedType);
        }
    }
}
