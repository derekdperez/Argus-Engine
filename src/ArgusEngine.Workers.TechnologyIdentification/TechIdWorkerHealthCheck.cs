using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.TechnologyIdentification;

public class TechIdWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly ILogger<TechIdWorkerHealthCheck> _logger;

    public TechIdWorkerHealthCheck(ILogger<TechIdWorkerHealthCheck> logger)
    {
        _logger = logger;
    }

    public string WorkerName => "TechnologyIdentification";

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Running TechID health check...");
        
        return new WorkerHealthCheckResult(true, "Technology Identification worker ready.");
    }
}
