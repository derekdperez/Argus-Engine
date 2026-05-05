using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Harness.Core.Workers;
using ArgusEngine.Workers.Enum.Consumers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Workers.Enum;

public class EnumWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly IOptions<SubdomainEnumerationOptions> _options;
    private readonly ILogger<EnumWorkerHealthCheck> _logger;

    public EnumWorkerHealthCheck(IOptions<SubdomainEnumerationOptions> options, ILogger<EnumWorkerHealthCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string WorkerName => "Enumeration";

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Running Enumeration health check...");
        
        var options = _options.Value;
        if (string.IsNullOrEmpty(options.SubfinderPath) && string.IsNullOrEmpty(options.AmassPath))
        {
            return new WorkerHealthCheckResult(false, "No subdomain enumeration providers (Subfinder/Amass) configured.");
        }

        // We could run a "help" command to verify binary presence
        // But for now, just checking config and basic reachability
        
        return new WorkerHealthCheckResult(true, $"Enumeration worker is ready. Configured providers: {(options.SubfinderPath != null ? "Subfinder " : "")}{(options.AmassPath != null ? "Amass" : "")}");
    }
}
