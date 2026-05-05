using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using ArgusEngine.Gatekeeper;
using ArgusEngine.Workers.Enum;
using ArgusEngine.Workers.Spider;
using ArgusEngine.Workers.PortScan;
using ArgusEngine.Workers.HighValue;
using ArgusEngine.Workers.TechnologyIdentification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Harness.Core;

public record HarnessResultDto(DateTimeOffset ExecutedAtUtc, List<WorkerHealthCheckResultDto> WorkerResults);
public record WorkerHealthCheckResultDto(string WorkerName, bool Success, string Message, string Output);

public class HarnessRunner
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HarnessRunner> _logger;

    public HarnessRunner(IServiceProvider serviceProvider, ILogger<HarnessRunner> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<HarnessResultDto> RunAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting Worker Test Harness run...");
        
        var results = new List<WorkerHealthCheckResultDto>();
        
        // We will manually instantiate them to ensure we exercise their logic 
        // without necessarily registering all their dependencies globally in Command Center.
        // However, some dependencies like IHttpClientFactory are already there.
        
        var healthChecks = new List<IWorkerHealthCheck>
        {
            ActivatorUtilities.CreateInstance<GatekeeperWorkerHealthCheck>(_serviceProvider),
            ActivatorUtilities.CreateInstance<EnumWorkerHealthCheck>(_serviceProvider),
            ActivatorUtilities.CreateInstance<SpiderWorkerHealthCheck>(_serviceProvider),
            ActivatorUtilities.CreateInstance<PortScanWorkerHealthCheck>(_serviceProvider),
            ActivatorUtilities.CreateInstance<HighValueWorkerHealthCheck>(_serviceProvider),
            ActivatorUtilities.CreateInstance<TechIdWorkerHealthCheck>(_serviceProvider)
        };

        foreach (var hc in healthChecks)
        {
            try
            {
                var result = await hc.RunAsync(ct).ConfigureAwait(false);
                results.Add(new WorkerHealthCheckResultDto(hc.WorkerName, result.Success, result.Message, result.Output));
            }
            catch (Exception ex)
            {
                results.Add(new WorkerHealthCheckResultDto(hc.WorkerName, false, $"Execution error: {ex.Message}", ex.ToString()));
            }
        }

        return new HarnessResultDto(DateTimeOffset.UtcNow, results);
    }
}
