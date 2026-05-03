using ArgusEngine.Contracts;

namespace ArgusEngine.Application.Gatekeeping;

public sealed record CanonicalAsset(AssetKind Kind, string CanonicalKey, string NormalizedDisplay);
