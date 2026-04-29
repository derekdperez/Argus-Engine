namespace NightmareV2.CommandCenter;

public sealed class NightmareRuntimeOptions
{
    public bool ListenPlainHttp { get; set; }
    public bool SkipStartupDatabase { get; set; }
    public DiagnosticsOptions Diagnostics { get; set; } = new();
    public DataMaintenanceOptions DataMaintenance { get; set; } = new();

    public sealed class DiagnosticsOptions
    {
        public bool Enabled { get; set; }
        public string ApiKey { get; set; } = "";
    }

    public sealed class DataMaintenanceOptions
    {
        public bool Enabled { get; set; }
        public string ApiKey { get; set; } = "";
    }
}
