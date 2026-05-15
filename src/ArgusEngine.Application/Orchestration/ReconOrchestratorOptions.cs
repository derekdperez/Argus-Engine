namespace ArgusEngine.Application.Orchestration;

public sealed class ReconOrchestratorOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 15;

    public int ClaimTimeoutSeconds { get; set; } = 120;

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

    public static ReconOrchestratorConfiguration FromOptions(ReconOrchestratorOptions options) =>
        new()
        {
            ReconProfilesPerTarget = Math.Clamp(options.ReconProfilesPerTarget, 1, 128),
            ReconProfilesPerSubdomain = Math.Clamp(options.ReconProfilesPerSubdomain, 1, 64),
            RequestsPerMinutePerSubdomain = Math.Clamp(options.RequestsPerMinutePerSubdomain, 1, 60_000),
            RandomDelayMin = Math.Max(0, options.RandomDelayMin),
            RandomDelayMax = Math.Max(Math.Max(0, options.RandomDelayMin), options.RandomDelayMax),
            RandomDelayEnabled = options.RandomDelayEnabled,
            RandomizeHeaderOrderEnabled = options.RandomizeHeaderOrderEnabled,
            ReconProfileDeviceTypes = Clean(options.ReconProfileDeviceTypes, ["mobile", "desktop", "tablet"]),
            ReconProfileBrowsers = Clean(options.ReconProfileBrowsers, ["firefox", "chrome", "safari"]),
            ReconProfileOs = Clean(options.ReconProfileOs, ["windows", "ios", "android", "chrome"]),
            ReconProfileHardwareAge = Math.Clamp(options.ReconProfileHardwareAge, 0, 25)
        };

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
