namespace ArgusEngine.Application.Orchestration;

public sealed class ReconOrchestratorOptions
{
    public bool Enabled { get; set; } = true;

    // New targets are not attached automatically unless this is explicitly enabled.
    public bool AutoAttachNewTargets { get; set; }

    public int PollIntervalSeconds { get; set; } = 15;

    public int ClaimTimeoutSeconds { get; set; } = 120;

    public int MaxTargetsPerTick { get; set; } = 25;

    public int MaxSubdomainsPerTick { get; set; } = 250;

    public int ProviderRunTimeoutSeconds { get; set; } = 3600;

    public int RequestedRunRetryDelaySeconds { get; set; } = 300;

    public int MaxRequestedRunRetries { get; set; } = 3;

    public int MaxHttpWorkersPerSubdomain { get; set; } = 1;

    public int RequestsPerSecondPerWorker { get; set; } = 2;

    public int MaxConcurrentSubdomainsPerWorker { get; set; } = 10;

    public int ReconProfilesPerTarget { get; set; } = 8;

    public int ReconProfilesPerSubdomain { get; set; } = 2;

    public int RequestsPerMinutePerSubdomain { get; set; } = 120;

    public double RandomDelayMin { get; set; } = 0.25;

    public double RandomDelayMax { get; set; } = 2.5;

    public bool RandomDelayEnabled { get; set; } = true;

    public bool RandomizeHeaderOrderEnabled { get; set; } = true;

    public string[] ReconProfileDeviceTypes { get; set; } = ["mobile", "desktop", "tablet"];

    public string[] ReconProfileBrowsers { get; set; } = ["firefox", "chrome", "safari"];

    public string[] ReconProfileOs { get; set; } = ["windows", "ios", "android", "chrome"];

    public int ReconProfileHardwareAge { get; set; } = 12;
}

public sealed record ReconOrchestratorConfiguration
{
    public int ReconProfilesPerTarget { get; init; } = 8;

    public int ReconProfilesPerSubdomain { get; init; } = 2;

    public int RequestsPerMinutePerSubdomain { get; init; } = 120;

    public double RandomDelayMin { get; init; } = 0.25;

    public double RandomDelayMax { get; init; } = 2.5;

    public bool RandomDelayEnabled { get; init; } = true;

    public bool RandomizeHeaderOrderEnabled { get; init; } = true;

    public string[] ReconProfileDeviceTypes { get; init; } = ["mobile", "desktop", "tablet"];

    public string[] ReconProfileBrowsers { get; init; } = ["firefox", "chrome", "safari"];

    public string[] ReconProfileOs { get; init; } = ["windows", "ios", "android", "chrome"];

    public int ReconProfileHardwareAge { get; init; } = 12;

    public int MaxHttpWorkersPerSubdomain { get; init; } = 1;

    public int RequestsPerSecondPerWorker { get; init; }

    public int MaxConcurrentSubdomainsPerWorker { get; init; } = 10;

    public static ReconOrchestratorConfiguration FromOptions(ReconOrchestratorOptions options) =>
        new()
        {
            ReconProfilesPerTarget = Math.Clamp(options.ReconProfilesPerTarget, 1, 128),
            ReconProfilesPerSubdomain = Math.Clamp(options.ReconProfilesPerSubdomain, 1, 64),
            RequestsPerMinutePerSubdomain = Math.Clamp(options.RequestsPerSecondPerWorker * 60, 1, 60_000),
            RandomDelayMin = Math.Max(0, options.RandomDelayMin),
            RandomDelayMax = Math.Max(Math.Max(0, options.RandomDelayMin), options.RandomDelayMax),
            RandomDelayEnabled = options.RandomDelayEnabled,
            RandomizeHeaderOrderEnabled = options.RandomizeHeaderOrderEnabled,
            ReconProfileDeviceTypes = Clean(options.ReconProfileDeviceTypes, ["mobile", "desktop", "tablet"]),
            ReconProfileBrowsers = Clean(options.ReconProfileBrowsers, ["firefox", "chrome", "safari"]),
            ReconProfileOs = Clean(options.ReconProfileOs, ["windows", "ios", "android", "chrome"]),
            ReconProfileHardwareAge = Math.Clamp(options.ReconProfileHardwareAge, 0, 25),
            MaxHttpWorkersPerSubdomain = Math.Clamp(options.MaxHttpWorkersPerSubdomain, 1, 128),
            RequestsPerSecondPerWorker = Math.Clamp(options.RequestsPerSecondPerWorker, 1, 1_000),
            MaxConcurrentSubdomainsPerWorker = Math.Clamp(options.MaxConcurrentSubdomainsPerWorker, 1, 1_000)
        };

    public static ReconOrchestratorConfiguration Sanitize(ReconOrchestratorConfiguration? configuration, ReconOrchestratorOptions fallback)
    {
        var source = configuration ?? FromOptions(fallback);

        return new ReconOrchestratorConfiguration
        {
            ReconProfilesPerTarget = Math.Clamp(source.ReconProfilesPerTarget, 1, 128),
            ReconProfilesPerSubdomain = Math.Clamp(source.ReconProfilesPerSubdomain, 1, 64),
            RequestsPerMinutePerSubdomain = Math.Clamp(
                source.RequestsPerSecondPerWorker > 0
                    ? source.RequestsPerSecondPerWorker * 60
                    : source.RequestsPerMinutePerSubdomain,
                1,
                60_000),
            RandomDelayMin = Math.Max(0, source.RandomDelayMin),
            RandomDelayMax = Math.Max(Math.Max(0, source.RandomDelayMin), source.RandomDelayMax),
            RandomDelayEnabled = source.RandomDelayEnabled,
            RandomizeHeaderOrderEnabled = source.RandomizeHeaderOrderEnabled,
            ReconProfileDeviceTypes = Clean(source.ReconProfileDeviceTypes, ["mobile", "desktop", "tablet"]),
            ReconProfileBrowsers = Clean(source.ReconProfileBrowsers, ["firefox", "chrome", "safari"]),
            ReconProfileOs = Clean(source.ReconProfileOs, ["windows", "ios", "android", "chrome"]),
            ReconProfileHardwareAge = Math.Clamp(source.ReconProfileHardwareAge, 0, 25),
            MaxHttpWorkersPerSubdomain = Math.Clamp(source.MaxHttpWorkersPerSubdomain, 1, 128),
            RequestsPerSecondPerWorker = Math.Clamp(
                source.RequestsPerSecondPerWorker > 0
                    ? source.RequestsPerSecondPerWorker
                    : (int)Math.Ceiling(source.RequestsPerMinutePerSubdomain / 60.0),
                1,
                1_000),
            MaxConcurrentSubdomainsPerWorker = Math.Clamp(source.MaxConcurrentSubdomainsPerWorker, 1, 1_000)
        };
    }

    private static string[] Clean(string[]? values, string[] fallback)
    {
        var cleaned = (values ?? [])
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
