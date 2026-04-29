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

        logger.LogInformation("subfinder started. RootDomain={RootDomain}", request.RootDomain);
        var result = await processRunner.RunAsync(
                opt.Subfinder.BinaryPath,
                ["-d", request.RootDomain, "-silent", "-json"],
                workingDirectory,
                TimeSpan.FromSeconds(Math.Clamp(opt.Subfinder.TimeoutSeconds, 5, 3600)),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            logger.LogWarning(
                "subfinder failed for {RootDomain}. ExitCode={ExitCode}. Error={Error}",
                request.RootDomain,
                result.ExitCode,
                result.Stderr);
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
        logger.LogInformation("subfinder completed. RootDomain={RootDomain}, RawResults={RawResults}", request.RootDomain, parsed.Count);
        return parsed;
    }
}
