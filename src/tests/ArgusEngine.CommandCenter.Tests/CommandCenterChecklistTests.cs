using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using Xunit;

namespace ArgusEngine.CommandCenter.Tests;

public sealed class AssetDiscoveredEnvelopeTests
{
    private static readonly Guid TargetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CorrelationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset OccurredAt = new(2026, 05, 08, 12, 30, 00, TimeSpan.FromHours(-4));

    [Fact]
    public void Constructor_preserves_discovery_identity_and_default_envelope_metadata()
    {
        var discovered = CreateDiscovery(AssetKind.Subdomain, "api.example.com", assetId: null);

        Assert.Equal(TargetId, discovered.TargetId);
        Assert.Equal("example.com", discovered.TargetRootDomain);
        Assert.Equal(4, discovered.GlobalMaxDepth);
        Assert.Equal(2, discovered.Depth);
        Assert.Equal(AssetKind.Subdomain, discovered.Kind);
        Assert.Equal("api.example.com", discovered.RawValue);
        Assert.Equal("unit-test", discovered.DiscoveredBy);
        Assert.Equal(CorrelationId, discovered.CorrelationId);
        Assert.Null(discovered.AssetId);
        Assert.Equal("2", discovered.SchemaVersion);
        Assert.Equal("argus-engine", discovered.Producer);
        Assert.Equal(OccurredAt, discovered.OccurredAtUtc);
        Assert.Equal(1.0m, discovered.Confidence);
    }

    [Fact]
    public void With_expression_can_record_asset_admission_without_losing_correlation_context()
    {
        var admittedAssetId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var discovered = CreateDiscovery(AssetKind.Url, "https://example.com/login", assetId: null);

        var admitted = discovered with
        {
            AdmissionStage = (AssetAdmissionStage)1,
            AssetId = admittedAssetId,
            Depth = discovered.Depth + 1
        };

        Assert.Equal(admittedAssetId, admitted.AssetId);
        Assert.Equal(discovered.CorrelationId, admitted.CorrelationId);
        Assert.Equal(discovered.CausationId, admitted.CausationId);
        Assert.Equal(discovered.TargetId, admitted.TargetId);
        Assert.Equal(discovered.RawValue, admitted.RawValue);
        Assert.Equal(3, admitted.Depth);
        Assert.NotEqual(discovered.AdmissionStage, admitted.AdmissionStage);
    }

    private static AssetDiscovered CreateDiscovery(AssetKind kind, string rawValue, Guid? assetId) =>
        new(
            TargetId: TargetId,
            TargetRootDomain: "example.com",
            GlobalMaxDepth: 4,
            Depth: 2,
            Kind: kind,
            RawValue: rawValue,
            DiscoveredBy: "unit-test",
            OccurredAt: OccurredAt,
            CorrelationId: CorrelationId,
            AdmissionStage: (AssetAdmissionStage)0,
            AssetId: assetId);
}
