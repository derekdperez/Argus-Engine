namespace ArgusEngine.Workers.Orchestration.Configuration;

public sealed class ReconOrchestratorOptions
{
    public const string SectionName = "ReconOrchestrator";

    public bool Enabled { get; set; } = true;

    public int MaxTargetsPerTick { get; set; } = 100;

    public int PollIntervalSeconds { get; set; } = 15;

    public int LeaseSeconds { get; set; } = 90;

    public bool ApplySchemaOnStartup { get; set; } = true;

    public bool PublishSpiderSeeds { get; set; } = true;

    public bool PublishPendingUrlResumes { get; set; } = true;

    public bool RequireEnumerationBeforeSpidering { get; set; } = true;

    public string DefaultMachineIdentity { get; set; } = "local-worker";

    public List<Guid> TargetIds { get; set; } = [];

    public List<string> EnumerationProviders { get; set; } =
    [
        "subfinder",
        "amass"
    ];

    public int ReconProfilesPerTarget { get; set; } = 8;

    public int ReconProfilesPerSubdomain { get; set; } = 2;

    public int RequestsPerMinutePerSubdomain { get; set; } = 120;

    public double RandomDelayMin { get; set; } = 0.25;

    public double RandomDelayMax { get; set; } = 2.5;

    public bool RandomDelayEnabled { get; set; } = true;

    public bool RandomizeHeaderOrderEnabled { get; set; } = true;

    public List<string> ReconProfileDeviceTypes { get; set; } =
    [
        "mobile",
        "desktop",
        "tablet"
    ];

    public List<string> ReconProfileBrowsers { get; set; } =
    [
        "firefox",
        "chrome",
        "safari"
    ];

    public List<string> ReconProfileOs { get; set; } =
    [
        "windows",
        "ios",
        "android",
        "chrome"
    ];

    /// <summary>
    /// Maximum age, in years, for generated hardware/software families.
    /// Versions older than this value are excluded from deterministic profile selection.
    /// </summary>
    public int ReconProfileHardwareAge { get; set; } = 12;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));

    public TimeSpan LeaseTtl => TimeSpan.FromSeconds(Math.Max(15, LeaseSeconds));
}
