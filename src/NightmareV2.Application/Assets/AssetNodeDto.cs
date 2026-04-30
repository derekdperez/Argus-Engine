using NightmareV2.Contracts;

namespace NightmareV2.Application.Assets;

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
