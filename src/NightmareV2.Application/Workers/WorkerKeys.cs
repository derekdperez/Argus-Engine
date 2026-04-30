namespace NightmareV2.Application.Workers;

public static class WorkerKeys
{
    /// <summary>Raw asset admissions (normalize, dedupe, Indexed fan-out). See <c>AssetDiscovered</c> Raw stage.</summary>
    public const string Gatekeeper = "Gatekeeper";
    public const string Spider = "Spider";
    /// <summary>Matches <c>worker_switches.worker_key</c> seeded as <c>Enum</c>.</summary>
    public const string Enumeration = "Enum";
    public const string PortScan = "PortScan";
    public const string HighValueRegex = "HighValueRegex";
    public const string HighValuePaths = "HighValuePaths";
    public const string TechnologyIdentification = "TechnologyIdentification";
}
