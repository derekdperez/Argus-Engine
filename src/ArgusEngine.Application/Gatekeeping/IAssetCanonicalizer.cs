using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Application.Gatekeeping;

public interface IAssetCanonicalizer
{
    CanonicalAsset Canonicalize(AssetDiscovered message);
}
