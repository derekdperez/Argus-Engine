namespace ArgusEngine.Workers.Orchestration.State;

public sealed class ReconWorkerProfile
{
    public string ProfileId { get; set; } = string.Empty;

    public Guid TargetId { get; set; }

    public string Subdomain { get; set; } = string.Empty;

    public string MachineIdentity { get; set; } = string.Empty;

    public string DeviceType { get; set; } = string.Empty;

    public string Browser { get; set; } = string.Empty;

    public string OperatingSystem { get; set; } = string.Empty;

    public string HardwareModel { get; set; } = string.Empty;

    public int BrowserMajorVersion { get; set; }

    public int OsMajorVersion { get; set; }

    public int RequestsPerMinute { get; set; }

    public bool RandomDelayEnabled { get; set; }

    public double RandomDelayMinSeconds { get; set; }

    public double RandomDelayMaxSeconds { get; set; }

    public bool RandomizeHeaderOrderEnabled { get; set; }

    public string UserAgent { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> HeaderOrder { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
