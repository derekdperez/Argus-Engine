using Microsoft.Extensions.Logging.Abstractions;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.Gatekeeping;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using Xunit;

namespace NightmareV2.Application.Tests;

public sealed class GatekeeperOrchestratorTests
{
    [Fact]
    public async Task ProcessAsync_RawInScopeAsset_PersistsAndPublishesIndexedEvent()
    {
        var fakes = new GatekeeperFakes();
        var message = RawAsset(AssetKind.Subdomain, "API.EXAMPLE.COM");

        await fakes.Create().ProcessAsync(message);

        Assert.Single(fakes.Persistence.PersistedMessages);
        var indexed = Assert.Single(fakes.Outbox.Messages.OfType<AssetDiscovered>());
        Assert.Equal(AssetAdmissionStage.Indexed, indexed.AdmissionStage);
        Assert.Equal("api.example.com", indexed.RawValue);
        Assert.Equal("gatekeeper", indexed.DiscoveredBy);
        Assert.Equal(fakes.Persistence.AssetId, indexed.AssetId);
        Assert.Equal(message.EventId, indexed.CausationId);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateWithoutRelationshipContext_SkipsPersistence()
    {
        var fakes = new GatekeeperFakes { Deduplicator = new FakeDeduplicator(reserveResult: false) };

        await fakes.Create().ProcessAsync(RawAsset(AssetKind.Subdomain, "api.example.com"));

        Assert.Empty(fakes.Persistence.PersistedMessages);
        Assert.Empty(fakes.Outbox.Messages);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateWithRelationshipContext_StillAttemptsPersistenceButDoesNotPublishWhenNotInserted()
    {
        var fakes = new GatekeeperFakes
        {
            Deduplicator = new FakeDeduplicator(reserveResult: false),
            Persistence = new FakePersistence(inserted: false),
        };
        var message = RawAsset(
            AssetKind.Url,
            "https://example.com/app.js",
            parentAssetId: Guid.NewGuid(),
            relationshipType: AssetRelationshipType.References);

        await fakes.Create().ProcessAsync(message);

        Assert.Single(fakes.Persistence.PersistedMessages);
        Assert.Empty(fakes.Outbox.Messages);
        Assert.Empty(fakes.Deduplicator.ReleasedKeys);
    }

    [Fact]
    public async Task ProcessAsync_OutOfScope_ReleasesReservedDedupeKey()
    {
        var fakes = new GatekeeperFakes { Scope = new FakeScope(isInScope: false) };

        await fakes.Create().ProcessAsync(RawAsset(AssetKind.Url, "https://evil.example.net"));

        Assert.Empty(fakes.Persistence.PersistedMessages);
        Assert.Single(fakes.Deduplicator.ReleasedKeys);
        Assert.Empty(fakes.Outbox.Messages);
    }

    [Fact]
    public async Task ProcessAsync_PersistenceFailure_ReleasesReservedKeyAndRethrows()
    {
        var fakes = new GatekeeperFakes { Persistence = new FakePersistence(throwOnPersist: true) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fakes.Create().ProcessAsync(RawAsset(AssetKind.Url, "https://example.com")));

        Assert.Single(fakes.Deduplicator.ReleasedKeys);
    }

    [Fact]
    public async Task ProcessAsync_IndexedOrDepthExceededOrDisabled_DoesNothing()
    {
        var indexedFakes = new GatekeeperFakes();
        await indexedFakes.Create().ProcessAsync(RawAsset(AssetKind.Subdomain, "api.example.com") with { AdmissionStage = AssetAdmissionStage.Indexed });
        Assert.Empty(indexedFakes.Persistence.PersistedMessages);

        var depthFakes = new GatekeeperFakes();
        await depthFakes.Create().ProcessAsync(RawAsset(AssetKind.Subdomain, "deep.example.com") with { Depth = 13, GlobalMaxDepth = 12 });
        Assert.Empty(depthFakes.Persistence.PersistedMessages);

        var disabledFakes = new GatekeeperFakes { Toggles = new FakeWorkerToggleReader(enabled: false) };
        await disabledFakes.Create().ProcessAsync(RawAsset(AssetKind.Subdomain, "api.example.com"));
        Assert.Empty(disabledFakes.Persistence.PersistedMessages);
    }

    [Fact]
    public async Task ProcessAsync_IndexedIp_QueuesPortScanOnlyWhenPortScanEnabled()
    {
        var enabled = new GatekeeperFakes();
        await enabled.Create().ProcessAsync(RawAsset(AssetKind.IpAddress, "203.0.113.5"));

        Assert.Single(enabled.Outbox.Messages.OfType<AssetDiscovered>());
        var portScan = Assert.Single(enabled.Outbox.Messages.OfType<PortScanRequested>());
        Assert.Equal("203.0.113.5", portScan.HostOrIp);

        var disabled = new GatekeeperFakes
        {
            Toggles = new FakeWorkerToggleReader(
                enabledByKey: new Dictionary<string, bool>
                {
                    [WorkerKeys.Gatekeeper] = true,
                    [WorkerKeys.PortScan] = false,
                }),
        };
        await disabled.Create().ProcessAsync(RawAsset(AssetKind.IpAddress, "203.0.113.5"));
        Assert.Empty(disabled.Outbox.Messages.OfType<PortScanRequested>());
    }

    private static AssetDiscovered RawAsset(
        AssetKind kind,
        string raw,
        Guid? parentAssetId = null,
        AssetRelationshipType? relationshipType = null) =>
        new(
            Guid.NewGuid(),
            "example.com",
            12,
            1,
            kind,
            raw,
            "test",
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid(),
            AssetAdmissionStage.Raw,
            AssetId: null,
            DiscoveryContext: "test discovery",
            ParentAssetId: parentAssetId,
            RelationshipType: relationshipType,
            EventId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            Producer: "test");

    private sealed class GatekeeperFakes
    {
        public FakeDeduplicator Deduplicator { get; init; } = new();
        public FakeScope Scope { get; init; } = new();
        public FakePersistence Persistence { get; init; } = new();
        public CapturingEventOutbox Outbox { get; } = new();
        public FakeWorkerToggleReader Toggles { get; init; } = new(enabled: true);

        public GatekeeperOrchestrator Create() =>
            new(
                new SimpleCanonicalizer(),
                Deduplicator,
                Scope,
                Persistence,
                Outbox,
                Toggles,
                NullLogger<GatekeeperOrchestrator>.Instance);
    }

    private sealed class SimpleCanonicalizer : IAssetCanonicalizer
    {
        public CanonicalAsset Canonicalize(AssetDiscovered message)
        {
            var normalized = message.RawValue.Trim().TrimEnd('/').TrimEnd('.').ToLowerInvariant();
            return new CanonicalAsset(message.Kind, $"{message.Kind}:{normalized}", normalized);
        }
    }

    private sealed class FakeDeduplicator(bool reserveResult = true) : IAssetDeduplicator
    {
        public List<string> ReservedKeys { get; } = [];
        public List<string> ReleasedKeys { get; } = [];

        public Task<bool> TryReserveAsync(Guid targetId, string canonicalKey, CancellationToken cancellationToken = default)
        {
            ReservedKeys.Add(canonicalKey);
            return Task.FromResult(reserveResult);
        }

        public Task ReleaseAsync(Guid targetId, string canonicalKey, CancellationToken cancellationToken = default)
        {
            ReleasedKeys.Add(canonicalKey);
            return Task.CompletedTask;
        }

        public Task ClearForTargetAsync(Guid targetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeScope(bool isInScope = true) : ITargetScopeEvaluator
    {
        public bool IsInScope(AssetDiscovered message, CanonicalAsset canonical) => isInScope;
    }

    private sealed class FakePersistence(bool inserted = true, bool throwOnPersist = false) : IAssetPersistence
    {
        public Guid AssetId { get; } = Guid.NewGuid();
        public List<AssetDiscovered> PersistedMessages { get; } = [];

        public Task<(Guid AssetId, bool Inserted)> PersistNewAssetAsync(
            AssetDiscovered message,
            CanonicalAsset canonical,
            CancellationToken cancellationToken = default)
        {
            if (throwOnPersist)
                throw new InvalidOperationException("persist failed");

            PersistedMessages.Add(message);
            return Task.FromResult((AssetId, inserted));
        }

        public Task ConfirmUrlAssetAsync(
            Guid assetId,
            UrlFetchSnapshot snapshot,
            Guid correlationId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CapturingEventOutbox : IEventOutbox
    {
        public List<IEventEnvelope> Messages { get; } = [];

        public Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : class, IEventEnvelope
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkerToggleReader : IWorkerToggleReader
    {
        private readonly bool _defaultEnabled;
        private readonly IReadOnlyDictionary<string, bool> _enabledByKey;

        public FakeWorkerToggleReader(bool enabled = true)
        {
            _defaultEnabled = enabled;
            _enabledByKey = new Dictionary<string, bool>();
        }

        public FakeWorkerToggleReader(IReadOnlyDictionary<string, bool> enabledByKey)
        {
            _defaultEnabled = true;
            _enabledByKey = enabledByKey;
        }

        public Task<bool> IsWorkerEnabledAsync(string workerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_enabledByKey.TryGetValue(workerKey, out var enabled) ? enabled : _defaultEnabled);
    }
}
