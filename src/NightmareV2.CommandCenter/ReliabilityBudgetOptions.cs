namespace NightmareV2.CommandCenter;

public sealed class ReliabilityBudgetOptions
{
    public decimal MinEventProcessingSuccessRate { get; set; } = 0.995m;
    public long MaxQueueBacklogAgeSeconds { get; set; } = 900;
    public long MinQueueDrainPerHour { get; set; } = 100;
    public long MaxWorkerErrorsPerHour { get; set; } = 25;

    public string[] ServiceNames { get; set; } =
    [
        "command-center",
        "gatekeeper",
        "worker-enum",
        "worker-spider",
        "worker-portscan",
        "worker-highvalue",
    ];
}
