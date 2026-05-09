using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using Xunit;

namespace ArgusEngine.ArchitectureTests;

public sealed class ProjectDependencyRulesTests
{
    [Fact]
    public void Event_contracts_expose_the_required_envelope_metadata()
    {
        var eventTypes = typeof(IEventEnvelope).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           typeof(IEventEnvelope).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(eventTypes);
        Assert.Contains(typeof(AssetDiscovered), eventTypes);

        foreach (var eventType in eventTypes)
        {
            Assert.NotNull(eventType.GetProperty(nameof(IEventEnvelope.EventId)));
            Assert.NotNull(eventType.GetProperty(nameof(IEventEnvelope.CorrelationId)));
            Assert.NotNull(eventType.GetProperty(nameof(IEventEnvelope.CausationId)));
            Assert.NotNull(eventType.GetProperty(nameof(IEventEnvelope.OccurredAtUtc)));
            Assert.NotNull(eventType.GetProperty(nameof(IEventEnvelope.SchemaVersion)));
            Assert.NotNull(eventType.GetProperty(nameof(IEventEnvelope.Producer)));
        }
    }

    [Fact]
    public void Persisted_asset_kind_values_remain_stable()
    {
        Assert.Equal(-1, (int)AssetKind.Target);
        Assert.Equal(0, (int)AssetKind.Domain);
        Assert.Equal(1, (int)AssetKind.Subdomain);
        Assert.Equal(10, (int)AssetKind.Url);
        Assert.Equal(11, (int)AssetKind.ApiEndpoint);
        Assert.Equal(12, (int)AssetKind.JavaScriptFile);
        Assert.Equal(33, (int)AssetKind.MarkdownBody);
    }

    [Fact]
    public void Event_contract_names_are_unique_inside_the_contract_assembly()
    {
        var duplicateNames = typeof(IEventEnvelope).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           typeof(IEventEnvelope).IsAssignableFrom(type))
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicateNames);
    }
}
