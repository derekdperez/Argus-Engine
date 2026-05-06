using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using ArgusEngine.Application.Gatekeeping;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Gatekeeper;

public partial class GatekeeperWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly GatekeeperOrchestrator _orchestrator;
    private readonly ILogger<GatekeeperWorkerHealthCheck> _logger;

    public GatekeeperWorkerHealthCheck(GatekeeperOrchestrator orchestrator, ILogger<GatekeeperWorkerHealthCheck> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public string WorkerName => "Gatekeeper";

    [LoggerMessage(Level = LogLevel.Information, Message = "Running Gatekeeper health check...")]
    private partial void LogRunningHealthCheck();

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        LogRunningHealthCheck();
        
        // Gatekeeper orchestrator check
        if (_orchestrator == null)
        {
            return new WorkerHealthCheckResult(false, "GatekeeperOrchestrator not initialized.");
        }

        return new WorkerHealthCheckResult(true, "Gatekeeper worker is operational.");
    }
}
