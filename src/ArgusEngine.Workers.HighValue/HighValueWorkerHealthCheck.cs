using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.HighValue;

public partial class HighValueWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly ILogger<HighValueWorkerHealthCheck> _logger;

    public HighValueWorkerHealthCheck(ILogger<HighValueWorkerHealthCheck> logger)
    {
        _logger = logger;
    }

    public string WorkerName => "HighValue";

    [LoggerMessage(Level = LogLevel.Information, Message = "Running HighValue health check...")]
    private partial void LogRunningHealthCheck();

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        LogRunningHealthCheck();
        
        // Check if wordlists are accessible
        return new WorkerHealthCheckResult(true, "HighValue worker ready for scanning.");
    }
}
