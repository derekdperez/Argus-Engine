using NightmareV2.Contracts;

namespace NightmareV2.Application.Assets;

public static class AssetKindClassification
{
    public static AssetCategory CategoryFor(AssetKind kind) =>
        kind switch
        {
            AssetKind.Target => AssetCategory.ScopeRoot,
            AssetKind.Domain or AssetKind.Subdomain => AssetCategory.Host,
            AssetKind.IpAddress or AssetKind.CidrBlock or AssetKind.Asn or AssetKind.OpenPort => AssetCategory.Network,
            AssetKind.TlsCertificate => AssetCategory.TransportSecurity,
            AssetKind.Url or AssetKind.ApiEndpoint or AssetKind.JavaScriptFile or AssetKind.MarkdownBody => AssetCategory.WebSurface,
            AssetKind.ApiMethod or AssetKind.Parameter => AssetCategory.ApiShape,
            AssetKind.Secret or AssetKind.Email or AssetKind.CloudBucket => AssetCategory.Signal,
            _ => AssetCategory.Signal,
        };
}
