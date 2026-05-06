using System;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using ArgusEngine.Workers.Enum.Consumers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Workers.Enumeration;

public partial class EnumWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly IOptions<SubdomainEnumerationOptions> _options;
    private readonly ILogger<EnumWorkerHealthCheck> _logger;

    public EnumWorkerHealthCheck(IOptions<SubdomainEnumerationOptions> options, ILogger<EnumWorkerHealthCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string WorkerName => "Enumeration";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enumeration worker degraded: no external tools (subfinder/amass) found at configured paths. {Details}")]
    private partial void LogWorkerDegraded(string details);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enumeration worker health check: {Details}")]
    private partial void LogHealthCheck(string details);

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var subfinderExists = !string.IsNullOrEmpty(options.Subfinder.BinaryPath) && File.Exists(options.Subfinder.BinaryPath);
        var amassExists = !string.IsNullOrEmpty(options.Amass.BinaryPath) && File.Exists(options.Amass.BinaryPath);
        var wordlistExists = !string.IsNullOrEmpty(options.Amass.WordlistPath) && File.Exists(options.Amass.WordlistPath);

        var details = $"Configured providers: subfinder={subfinderExists}, amass={amassExists}, wordlist={wordlistExists}.";
        
        if (!subfinderExists && !amassExists)
        {
            LogWorkerDegraded(details);
            return new WorkerHealthCheckResult(true, "Degraded: No enumeration tools found. " + details);
        }

        LogHealthCheck(details);
        return new WorkerHealthCheckResult(true, "Ready. " + details);
    }
}
