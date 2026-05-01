using NightmareV2.Application.Workers;
using NightmareV2.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightmareV2.Infrastructure.Workers;

public sealed class SubfinderEnumerationProvider(
    IOptions<SubdomainEnumerationOptions> options,
    ToolProcessRunner processRunner,
    ILogger<SubfinderEnumerationProvider> logger) : ISubdomainEnumerationProvider
{
    private static readonly Action<ILogger, string, Exception?> LogSubfinderStarted =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, nameof(LogSubfinderStarted)),
            "subfinder started. RootDomain={RootDomain}");
    private static readonly Action<ILogger, string, int?, string, Exception?> LogSubfinderFailed =
        LoggerMessage.Define<string, int?, string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogSubfinderFailed)),
            "subfinder failed for {RootDomain}. ExitCode={ExitCode}. Error={Error}");
    private static readonly Action<ILogger, string, int, Exception?> LogSubfinderCompleted =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(3, nameof(LogSubfinderCompleted)),
            "subfinder completed. RootDomain={RootDomain}, RawResults={RawResults}");

    public string Name => "subfinder";

    public async Task<IReadOnlyCollection<SubdomainEnumerationResult>> EnumerateAsync(
        SubdomainEnumerationRequested request,
        CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (!opt.Subfinder.Enabled)
            return [];

        var workingDirectory = opt.WorkingDirectory;
        Directory.CreateDirectory(workingDirectory);

        LogSubfinderStarted(logger, request.RootDomain, null);
        var result = await processRunner.RunAsync(
                opt.Subfinder.BinaryPath,
                ["-d", request.RootDomain, "-silent", "-json"],
                workingDirectory,
                TimeSpan.FromSeconds(Math.Clamp(opt.Subfinder.TimeoutSeconds, 5, 3600)),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            LogSubfinderFailed(logger, request.RootDomain, result.ExitCode, result.Stderr, null);
            return [];
        }

        var parsed = SubdomainEnumerationParsers.ParseSubfinderOutput(result.Stdout)
            .Select(
                host => new SubdomainEnumerationResult
                {
                    Hostname = host,
                    Provider = Name,
                    Method = "passive",
                })
            .ToList();
        LogSubfinderCompleted(logger, request.RootDomain, parsed.Count, null);
        return parsed;
    }
}
