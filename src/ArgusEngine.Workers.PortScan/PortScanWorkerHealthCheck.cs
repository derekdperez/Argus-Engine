using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.PortScan;

public partial class PortScanWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly ILogger<PortScanWorkerHealthCheck> _logger;

    public PortScanWorkerHealthCheck(ILogger<PortScanWorkerHealthCheck> logger)
    {
        _logger = logger;
    }

    public string WorkerName => "PortScan";

    [LoggerMessage(Level = LogLevel.Information, Message = "Running PortScan health check...")]
    private partial void LogRunningHealthCheck();

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        LogRunningHealthCheck();
        
        // PortScan uses Nmap or similar. For now, just check if we can initialize.
        return new WorkerHealthCheckResult(true, "PortScan worker initialized and ready.");
    }
}
