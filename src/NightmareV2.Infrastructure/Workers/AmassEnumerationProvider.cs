using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Infrastructure.Workers;

public sealed class AmassEnumerationProvider(
    IOptions<SubdomainEnumerationOptions> options,
    ToolProcessRunner processRunner,
    IHostResolver hostResolver,
    ILogger<AmassEnumerationProvider> logger) : ISubdomainEnumerationProvider
{
    public string Name => "amass";

    public async Task<IReadOnlyCollection<SubdomainEnumerationResult>> EnumerateAsync(
        SubdomainEnumerationRequested request,
        CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (!opt.Amass.Enabled)
            return [];

        var workingDirectory = opt.WorkingDirectory;
        Directory.CreateDirectory(workingDirectory);

        var wordlistPath = ResolveWordlistPath(opt.Amass.WordlistPath);
        logger.LogInformation("amass started. RootDomain={RootDomain}, WordlistPath={WordlistPath}", request.RootDomain, wordlistPath);
        if (!File.Exists(wordlistPath))
        {
            logger.LogWarning(
                "Amass wordlist was not found. RootDomain={RootDomain}, WordlistPath={WordlistPath}",
                request.RootDomain,
                wordlistPath);
            return [];
        }

        var safeDomain = SubdomainEnumerationNormalization.MakeSafeFileName(request.RootDomain);
        var safeJobId = SubdomainEnumerationNormalization.MakeSafeFileName(
            $"{request.Provider}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var outputFile = Path.Combine(workingDirectory, $"amass-{safeDomain}-{safeJobId}.txt");
        var logFile = Path.Combine(workingDirectory, $"amass-{safeDomain}-{safeJobId}.log");

        var args = new List<string> { "enum" };
        if (opt.Amass.Active)
            args.Add("-active");
        if (opt.Amass.BruteForce)
            args.Add("-brute");
        args.AddRange(
        [
            "-d", request.RootDomain,
            "-w", wordlistPath,
            "-dns-qps", Math.Clamp(opt.Amass.DnsQueriesPerSecond, 1, 10000).ToString(CultureInfo.InvariantCulture),
            "-max-depth", Math.Clamp(opt.Amass.MaxDepth, 1, 10).ToString(CultureInfo.InvariantCulture),
            "-min-for-recursive", Math.Clamp(opt.Amass.MinForRecursive, 1, 50).ToString(CultureInfo.InvariantCulture),
            "-timeout", Math.Clamp(opt.Amass.TimeoutMinutes, 1, 240).ToString(CultureInfo.InvariantCulture),
            "-o", outputFile,
            "-log", logFile,
        ]);

        var result = await processRunner.RunAsync(
                opt.Amass.BinaryPath,
                args,
                workingDirectory,
                TimeSpan.FromMinutes(Math.Clamp(opt.Amass.TimeoutMinutes, 1, 240) + 5),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            logger.LogWarning(
                "amass failed for {RootDomain}. ExitCode={ExitCode}. Error={Error}",
                request.RootDomain,
                result.ExitCode,
                result.Stderr);
            return [];
        }

        var wildcardDetected = await DetectWildcardDnsAsync(request.RootDomain, cancellationToken).ConfigureAwait(false);
        if (wildcardDetected)
        {
            logger.LogWarning(
                "Wildcard DNS detected for {RootDomain}. Provider={Provider}",
                request.RootDomain,
                Name);
        }

        var parsed = SubdomainEnumerationParsers.ParseAmassOutputFile(outputFile)
            .Select(
                host => new SubdomainEnumerationResult
                {
                    Hostname = host,
                    Provider = Name,
                    Method = "active-bruteforce",
                })
            .ToList();
        logger.LogInformation("amass completed. RootDomain={RootDomain}, RawResults={RawResults}", request.RootDomain, parsed.Count);
        return parsed;
    }

    internal async Task<bool> DetectWildcardDnsAsync(string rootDomain, CancellationToken cancellationToken)
    {
        var samples = new List<IReadOnlyCollection<string>>(capacity: 3);
        for (var i = 0; i < 3; i++)
        {
            var randomHost = $"random-{Guid.NewGuid():N}.{rootDomain}";
            try
            {
                var addresses = await hostResolver.ResolveHostAsync(randomHost, cancellationToken).ConfigureAwait(false);
                if (addresses.Count > 0)
                    samples.Add(addresses.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }
            catch
            {
                // Failures are expected for non-wildcard domains.
            }
        }

        if (samples.Count < 2)
            return false;

        var first = string.Join("|", samples[0]);
        var same = samples.LongCount(x => string.Equals(first, string.Join("|", x.OrderBy(a => a, StringComparer.Ordinal)), StringComparison.Ordinal));
        return same >= 2;
    }

    private static string ResolveWordlistPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;
        return Path.Combine(AppContext.BaseDirectory, configuredPath);
    }
}
