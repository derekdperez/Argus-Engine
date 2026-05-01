using NightmareV2.Application.Assets;
using NightmareV2.Contracts;
using Xunit;

namespace NightmareV2.Application.Tests;

public sealed class AssetRelationshipRulesTests
{
    [Theory]
    [InlineData(AssetKind.Target, AssetKind.Domain, AssetRelationshipType.Contains)]
    [InlineData(AssetKind.Domain, AssetKind.IpAddress, AssetRelationshipType.ResolvesTo)]
    [InlineData(AssetKind.Subdomain, AssetKind.JavaScriptFile, AssetRelationshipType.Contains)]
    [InlineData(AssetKind.Url, AssetKind.Secret, AssetRelationshipType.ExtractedFrom)]
    [InlineData(AssetKind.ApiEndpoint, AssetKind.ApiMethod, AssetRelationshipType.Defines)]
    [InlineData(AssetKind.ApiMethod, AssetKind.Parameter, AssetRelationshipType.Defines)]
    [InlineData(AssetKind.JavaScriptFile, AssetKind.ApiEndpoint, AssetRelationshipType.References)]
    public void IsAllowed_AllowsExpectedGraphEdges(
        AssetKind parent,
        AssetKind child,
        AssetRelationshipType relationship)
    {
        Assert.True(AssetRelationshipRules.IsAllowed(parent, child, relationship));
    }

    [Theory]
    [InlineData(AssetKind.Domain, AssetKind.Domain, AssetRelationshipType.Contains)]
    [InlineData(AssetKind.Target, AssetKind.Secret, AssetRelationshipType.Contains)]
    [InlineData(AssetKind.Url, AssetKind.IpAddress, AssetRelationshipType.ResolvesTo)]
    [InlineData(AssetKind.ApiEndpoint, AssetKind.Secret, AssetRelationshipType.ExtractedFrom)]
    [InlineData(AssetKind.OpenPort, AssetKind.Parameter, AssetRelationshipType.Defines)]
    public void IsAllowed_RejectsInvalidOrAmbiguousGraphEdges(
        AssetKind parent,
        AssetKind child,
        AssetRelationshipType relationship)
    {
        Assert.False(AssetRelationshipRules.IsAllowed(parent, child, relationship));
    }

    [Theory]
    [InlineData(AssetKind.Target, AssetCategory.ScopeRoot)]
    [InlineData(AssetKind.Subdomain, AssetCategory.Host)]
    [InlineData(AssetKind.OpenPort, AssetCategory.Network)]
    [InlineData(AssetKind.Url, AssetCategory.WebSurface)]
    [InlineData(AssetKind.Parameter, AssetCategory.ApiShape)]
    [InlineData(AssetKind.Secret, AssetCategory.Signal)]
    public void CategoryFor_MapsKindsToStableOperatorCategories(AssetKind kind, AssetCategory expected)
    {
        Assert.Equal(expected, AssetKindClassification.CategoryFor(kind));
    }
}
