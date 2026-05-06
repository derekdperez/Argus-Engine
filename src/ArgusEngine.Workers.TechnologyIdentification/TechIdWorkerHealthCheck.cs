using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.TechnologyIdentification;

public partial class TechIdWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly ILogger<TechIdWorkerHealthCheck> _logger;

    public TechIdWorkerHealthCheck(ILogger<TechIdWorkerHealthCheck> logger)
    {
        _logger = logger;
    }

    public string WorkerName => "TechnologyIdentification";

    [LoggerMessage(Level = LogLevel.Information, Message = "Running TechID health check...")]
    private partial void LogRunningHealthCheck();

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        LogRunningHealthCheck();
        
        return new WorkerHealthCheckResult(true, "Technology Identification worker ready.");
    }
}
