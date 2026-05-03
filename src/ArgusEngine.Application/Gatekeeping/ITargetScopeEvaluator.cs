using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Application.Gatekeeping;

public interface ITargetScopeEvaluator
{
    bool IsInScope(AssetDiscovered message, CanonicalAsset canonical);
}
