namespace ArgusEngine.Contracts;

/// <summary>
/// Coarse-grained grouping used for hierarchy validation, filtering, and UI presentation.
/// </summary>
public enum AssetCategory
{
    ScopeRoot = 0,
    Host = 1,
    Network = 2,
    TransportSecurity = 3,
    WebSurface = 4,
    ApiShape = 5,
    Signal = 6,
}
