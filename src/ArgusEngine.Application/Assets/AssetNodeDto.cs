using ArgusEngine.Contracts;

namespace ArgusEngine.Application.Assets;

public sealed record AssetNodeDto(
    Guid Id,
    Guid TargetId,
    AssetKind Kind,
    AssetCategory Category,
    string CanonicalKey,
    string RawValue,
    string LifecycleStatus,
    string? DisplayName,
    string? TypeDetailsJson);
