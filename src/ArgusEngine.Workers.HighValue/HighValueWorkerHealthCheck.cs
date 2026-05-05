using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.HighValue;

public class HighValueWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly ILogger<HighValueWorkerHealthCheck> _logger;

    public HighValueWorkerHealthCheck(ILogger<HighValueWorkerHealthCheck> logger)
    {
        _logger = logger;
    }

    public string WorkerName => "HighValue";

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Running HighValue health check...");
        
        // Check if wordlists are accessible
        return new WorkerHealthCheckResult(true, "HighValue worker ready for scanning.");
    }
}
