using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Infrastructure.Workers;

public sealed class AmassEnumerationProvider(
    IOptions<SubdomainEnumerationOptions> options,
    ToolProcessRunner processRunner,
    IHostResolver hostResolver,
    ILogger<AmassEnumerationProvider> logger) : ISubdomainEnumerationProvider
{
    private static readonly Action<ILogger, string, string, Exception?> LogAmassStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogAmassStarted)),
            "amass started. RootDomain={RootDomain}, WordlistPath={WordlistPath}");
    private static readonly Action<ILogger, string, string, Exception?> LogAmassWordlistMissing =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogAmassWordlistMissing)),
            "Amass wordlist was not found. RootDomain={RootDomain}, WordlistPath={WordlistPath}");
    private static readonly Action<ILogger, string, int?, string, Exception?> LogAmassFailed =
        LoggerMessage.Define<string, int?, string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogAmassFailed)),
            "amass failed for {RootDomain}. ExitCode={ExitCode}. Error={Error}");
    private static readonly Action<ILogger, string, string, Exception?> LogWildcardDnsDetected =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogWildcardDnsDetected)),
            "Wildcard DNS detected for {RootDomain}. Provider={Provider}");
    private static readonly Action<ILogger, string, int, Exception?> LogAmassCompleted =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(5, nameof(LogAmassCompleted)),
            "amass completed. RootDomain={RootDomain}, RawResults={RawResults}");

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
        LogAmassStarted(logger, request.RootDomain, wordlistPath, null);
        if (!File.Exists(wordlistPath))
        {
            LogAmassWordlistMissing(logger, request.RootDomain, wordlistPath, null);
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
            LogAmassFailed(logger, request.RootDomain, result.ExitCode, result.Stderr, null);
            return [];
        }

        var wildcardDetected = await DetectWildcardDnsAsync(request.RootDomain, cancellationToken).ConfigureAwait(false);
        if (wildcardDetected)
        {
            LogWildcardDnsDetected(logger, request.RootDomain, Name, null);
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
        LogAmassCompleted(logger, request.RootDomain, parsed.Count, null);
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
