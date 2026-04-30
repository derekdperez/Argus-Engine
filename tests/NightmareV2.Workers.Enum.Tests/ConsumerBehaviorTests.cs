using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Workers.Enum.Consumers;
using Xunit;

namespace NightmareV2.Workers.Enum.Tests;

public sealed class ConsumerBehaviorTests
{
    [Fact]
    public async Task TargetCreatedConsumer_QueuesSubfinderAndAmassByDefault()
    {
        var outbox = new CapturingEventOutbox();
        var toggles = Mock.Of<IWorkerToggleReader>(x => x.IsWorkerEnabledAsync(WorkerKeys.Enumeration, It.IsAny<CancellationToken>()) == Task.FromResult(true));
        var inbox = Mock.Of<IInboxDeduplicator>(x => x.TryBeginProcessingAsync(It.IsAny<IEventEnvelope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult(true));
        var options = Options.Create(new SubdomainEnumerationOptions());
        var consumer = new TargetCreatedConsumer(
            Mock.Of<ILogger<TargetCreatedConsumer>>(),
            toggles,
            inbox,
            outbox,
            options);

        var message = new TargetCreated(
            Guid.NewGuid(),
            "example.com",
            12,
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            EventId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            Producer: "test");
        var context = BuildContext(message);

        await consumer.Consume(context.Object);

        var jobs = outbox.Messages.OfType<SubdomainEnumerationRequested>().ToList();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, x => x.Provider == "subfinder");
        Assert.Contains(jobs, x => x.Provider == "amass");
    }

    [Fact]
    public async Task EnumerationRequestedConsumer_RunsOnlyRequestedProvider()
    {
        var outbox = new CapturingEventOutbox();
        var toggles = Mock.Of<IWorkerToggleReader>(x => x.IsWorkerEnabledAsync(WorkerKeys.Enumeration, It.IsAny<CancellationToken>()) == Task.FromResult(true));
        var targetLookup = Mock.Of<ITargetLookup>(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<TargetLookupResult?>(new TargetLookupResult(Guid.NewGuid(), "example.com", 12)));
        var graph = Mock.Of<IAssetGraphService>(x => x.GetRootAssetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<AssetNodeDto?>(null));

        var subfinder = new CountingProvider("subfinder", [
            new SubdomainEnumerationResult { Hostname = "api.example.com", Provider = "subfinder", Method = "passive" },
            new SubdomainEnumerationResult { Hostname = "dev.example.com", Provider = "subfinder", Method = "passive" }
        ]);
        var amass = new CountingProvider("amass", [
            new SubdomainEnumerationResult { Hostname = "admin.example.com", Provider = "amass", Method = "active-bruteforce" }
        ]);

        var consumer = new SubdomainEnumerationRequestedConsumer(
            Mock.Of<ILogger<SubdomainEnumerationRequestedConsumer>>(),
            [subfinder, amass],
            toggles,
            outbox,
            targetLookup,
            graph,
            Options.Create(new SubdomainEnumerationOptions()));

        var message = new SubdomainEnumerationRequested(
            Guid.NewGuid(),
            "example.com",
            "subfinder",
            "manual",
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            EventId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            Producer: "test");
        var context = BuildContext(message);

        await consumer.Consume(context.Object);

        Assert.Equal(1, subfinder.Calls);
        Assert.Equal(0, amass.Calls);
        var published = outbox.Messages.OfType<AssetDiscovered>().ToList();
        Assert.Equal(2, published.Count);
        Assert.All(published, e => Assert.Equal("enum-worker:subfinder", e.DiscoveredBy));
    }

    [Fact]
    public async Task EnumerationRequestedConsumer_ProviderFailureDoesNotThrow()
    {
        var outbox = new CapturingEventOutbox();
        var toggles = Mock.Of<IWorkerToggleReader>(x => x.IsWorkerEnabledAsync(WorkerKeys.Enumeration, It.IsAny<CancellationToken>()) == Task.FromResult(true));
        var targetLookup = Mock.Of<ITargetLookup>(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<TargetLookupResult?>(new TargetLookupResult(Guid.NewGuid(), "example.com", 12)));
        var graph = Mock.Of<IAssetGraphService>(x => x.GetRootAssetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<AssetNodeDto?>(null));

        var failingProvider = new ThrowingProvider("amass");
        var consumer = new SubdomainEnumerationRequestedConsumer(
            Mock.Of<ILogger<SubdomainEnumerationRequestedConsumer>>(),
            [failingProvider],
            toggles,
            outbox,
            targetLookup,
            graph,
            Options.Create(new SubdomainEnumerationOptions()));

        var message = new SubdomainEnumerationRequested(
            Guid.NewGuid(),
            "example.com",
            "amass",
            "manual",
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            EventId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            Producer: "test");
        var context = BuildContext(message);

        await consumer.Consume(context.Object);
        Assert.Empty(outbox.Messages.OfType<AssetDiscovered>());
    }

    [Fact]
    public async Task EnumerationRequestedConsumer_EnforcesMaxSubdomainsPerJob()
    {
        var outbox = new CapturingEventOutbox();
        var toggles = Mock.Of<IWorkerToggleReader>(x => x.IsWorkerEnabledAsync(WorkerKeys.Enumeration, It.IsAny<CancellationToken>()) == Task.FromResult(true));
        var targetLookup = Mock.Of<ITargetLookup>(x => x.FindAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<TargetLookupResult?>(new TargetLookupResult(Guid.NewGuid(), "example.com", 12)));
        var graph = Mock.Of<IAssetGraphService>(x => x.GetRootAssetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<AssetNodeDto?>(null));
        var subfinder = new CountingProvider(
            "subfinder",
            [
                new SubdomainEnumerationResult { Hostname = "api.example.com", Provider = "subfinder", Method = "passive" },
                new SubdomainEnumerationResult { Hostname = "dev.example.com", Provider = "subfinder", Method = "passive" }
            ]);

        var consumer = new SubdomainEnumerationRequestedConsumer(
            Mock.Of<ILogger<SubdomainEnumerationRequestedConsumer>>(),
            [subfinder],
            toggles,
            outbox,
            targetLookup,
            graph,
            Options.Create(
                new SubdomainEnumerationOptions
                {
                    MaxSubdomainsPerJob = 1,
                }));

        var context = BuildContext(
            new SubdomainEnumerationRequested(
                Guid.NewGuid(),
                "example.com",
                "subfinder",
                "manual",
                DateTimeOffset.UtcNow,
                CorrelationId: Guid.NewGuid(),
                EventId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                Producer: "test"));

        await consumer.Consume(context.Object);
        Assert.Single(outbox.Messages.OfType<AssetDiscovered>());
    }

    private static Mock<ConsumeContext<T>> BuildContext<T>(T message) where T : class
    {
        var context = new Mock<ConsumeContext<T>>();
        context.SetupGet(x => x.Message).Returns(message);
        context.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    private sealed class CountingProvider(string name, IReadOnlyCollection<SubdomainEnumerationResult> result) : ISubdomainEnumerationProvider
    {
        public string Name { get; } = name;
        public int Calls { get; private set; }

        public Task<IReadOnlyCollection<SubdomainEnumerationResult>> EnumerateAsync(SubdomainEnumerationRequested request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProvider(string name) : ISubdomainEnumerationProvider
    {
        public string Name { get; } = name;

        public Task<IReadOnlyCollection<SubdomainEnumerationResult>> EnumerateAsync(SubdomainEnumerationRequested request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
