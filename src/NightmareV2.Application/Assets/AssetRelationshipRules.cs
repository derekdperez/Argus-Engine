using NightmareV2.Contracts;

namespace NightmareV2.Application.Assets;

public static class AssetRelationshipRules
{
    public static bool IsAllowed(AssetKind parentKind, AssetKind childKind, AssetRelationshipType relationshipType)
    {
        if (parentKind == childKind && relationshipType == AssetRelationshipType.Contains)
            return false;

        return parentKind switch
        {
            AssetKind.Target =>
                relationshipType == AssetRelationshipType.Contains
                && childKind is AssetKind.Domain or AssetKind.Subdomain or AssetKind.CidrBlock or AssetKind.Asn or AssetKind.IpAddress,

            AssetKind.Domain =>
                childKind switch
                {
                    AssetKind.Subdomain => relationshipType == AssetRelationshipType.Contains,
                    AssetKind.IpAddress => relationshipType is AssetRelationshipType.ResolvesTo or AssetRelationshipType.Contains,
                    AssetKind.OpenPort => relationshipType is AssetRelationshipType.ServedBy or AssetRelationshipType.Contains,
                    AssetKind.TlsCertificate => relationshipType is AssetRelationshipType.ObservedOn or AssetRelationshipType.Contains,
                    AssetKind.Url or AssetKind.ApiEndpoint => relationshipType == AssetRelationshipType.Contains,
                    _ => false,
                },

            AssetKind.Subdomain =>
                childKind switch
                {
                    AssetKind.IpAddress => relationshipType is AssetRelationshipType.ResolvesTo or AssetRelationshipType.Contains,
                    AssetKind.OpenPort => relationshipType is AssetRelationshipType.ServedBy or AssetRelationshipType.Contains,
                    AssetKind.TlsCertificate => relationshipType is AssetRelationshipType.ObservedOn or AssetRelationshipType.Contains,
                    AssetKind.Url or AssetKind.ApiEndpoint or AssetKind.JavaScriptFile or AssetKind.MarkdownBody => relationshipType == AssetRelationshipType.Contains,
                    _ => false,
                },

            AssetKind.IpAddress =>
                childKind switch
                {
                    AssetKind.OpenPort => relationshipType is AssetRelationshipType.Contains or AssetRelationshipType.ServedBy,
                    AssetKind.TlsCertificate => relationshipType == AssetRelationshipType.ObservedOn,
                    _ => false,
                },

            AssetKind.OpenPort =>
                childKind switch
                {
                    AssetKind.TlsCertificate => relationshipType == AssetRelationshipType.ObservedOn,
                    AssetKind.Url or AssetKind.ApiEndpoint => relationshipType is AssetRelationshipType.ServedBy or AssetRelationshipType.Contains,
                    _ => false,
                },

            AssetKind.Url =>
                childKind switch
                {
                    AssetKind.JavaScriptFile or AssetKind.MarkdownBody or AssetKind.Parameter => relationshipType == AssetRelationshipType.Contains,
                    AssetKind.Secret or AssetKind.Email or AssetKind.CloudBucket => relationshipType == AssetRelationshipType.ExtractedFrom,
                    AssetKind.ApiEndpoint => relationshipType == AssetRelationshipType.References,
                    _ => false,
                },

            AssetKind.JavaScriptFile or AssetKind.MarkdownBody =>
                childKind switch
                {
                    AssetKind.Url or AssetKind.ApiEndpoint => relationshipType == AssetRelationshipType.References,
                    AssetKind.Secret or AssetKind.Email or AssetKind.CloudBucket => relationshipType == AssetRelationshipType.ExtractedFrom,
                    _ => false,
                },

            AssetKind.ApiEndpoint =>
                childKind switch
                {
                    AssetKind.ApiMethod => relationshipType == AssetRelationshipType.Defines,
                    AssetKind.Parameter => relationshipType is AssetRelationshipType.Defines or AssetRelationshipType.Contains,
                    _ => false,
                },

            AssetKind.ApiMethod =>
                childKind == AssetKind.Parameter && relationshipType == AssetRelationshipType.Defines,

            AssetKind.TlsCertificate =>
                childKind is AssetKind.Domain or AssetKind.Subdomain && relationshipType == AssetRelationshipType.References,

            AssetKind.CidrBlock =>
                childKind == AssetKind.IpAddress && relationshipType == AssetRelationshipType.Contains,

            AssetKind.Asn =>
                childKind is AssetKind.CidrBlock or AssetKind.IpAddress && relationshipType == AssetRelationshipType.Contains,

            _ => false,
        };
    }
}
