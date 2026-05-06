using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
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
        var options = _options.Value;
        var subfinderExists = !string.IsNullOrEmpty(options.Subfinder.BinaryPath) && File.Exists(options.Subfinder.BinaryPath);
        var amassExists = !string.IsNullOrEmpty(options.Amass.BinaryPath) && File.Exists(options.Amass.BinaryPath);
        var wordlistExists = !string.IsNullOrEmpty(options.Amass.WordlistPath) && File.Exists(options.Amass.WordlistPath);

        var details = $"Configured providers: subfinder={subfinderExists}, amass={amassExists}, wordlist={wordlistExists}.";
        
        if (!subfinderExists && !amassExists)
        {
            _logger.LogWarning("Enumeration worker degraded: no external tools (subfinder/amass) found at configured paths. {Details}", details);
            return new WorkerHealthCheckResult(true, "Degraded: No enumeration tools found. " + details);
        }

        _logger.LogInformation("Enumeration worker health check: {Details}", details);
        return new WorkerHealthCheckResult(true, "Ready. " + details);
    }
}
