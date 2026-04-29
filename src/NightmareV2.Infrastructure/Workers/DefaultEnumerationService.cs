using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts.Events;

namespace NightmareV2.Infrastructure.Workers;

public sealed class DefaultEnumerationService(
    IConfiguration configuration,
    ILogger<DefaultEnumerationService> logger) : IEnumerationService
{
    private static readonly string[] CandidatePrefixes =
    [
        "www",
        "api",
        "app",
        "admin",
        "portal",
        "login",
        "auth",
        "sso",
        "dev",
        "test",
        "stage",
        "staging",
        "qa",
        "beta",
        "demo",
        "cdn",
        "static",
        "assets",
        "img",
        "images",
        "m",
        "mobile",
        "mail",
        "smtp",
        "imap",
        "pop",
        "vpn",
        "remote",
        "git",
        "status",
        "docs",
        "support",
    ];

    public async Task<IReadOnlyList<string>> DiscoverSubdomainsAsync(
        TargetCreated target,
        CancellationToken cancellationToken = default)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = NormalizeHostCandidate(target.RootDomain);
        if (string.IsNullOrWhiteSpace(root))
            return [];

        if (configuration.GetValue("Enumeration:UseSubfinder", true))
        {
            await RunToolAsync(
                    ResolveConfiguredPath("Enumeration:SubfinderPath", "subfinder"),
                    BuildSubfinderArguments(root),
                    root,
                    discovered,
                    ResolveTimeout("Enumeration:SubfinderTimeoutSeconds", 180),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration.GetValue("Enumeration:UseAmass", true))
        {
            await RunToolAsync(
                    ResolveConfiguredPath("Enumeration:AmassPath", "amass"),
                    BuildAmassArguments(root),
                    root,
                    discovered,
                    ResolveTimeout("Enumeration:AmassTimeoutSeconds", 900),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration.GetValue("Enumeration:UseDnsFallback", true))
        {
            await DiscoverCommonDnsNamesAsync(root, discovered, cancellationToken).ConfigureAwait(false);
        }

        return discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<string> BuildSubfinderArguments(string root)
    {
        var args = new List<string> { "-silent" };
        if (configuration.GetValue("Enumeration:SubfinderAllSources", true))
            args.Add("-all");
        if (configuration.GetValue("Enumeration:SubfinderRecursive", true))
            args.Add("-recursive");

        args.Add("-d");
        args.Add(root);
        return args;
    }

    private IReadOnlyList<string> BuildAmassArguments(string root)
    {
        var args = new List<string> { "enum" };

        if (configuration.GetValue("Enumeration:AmassActive", true))
            args.Add("-active");
        if (configuration.GetValue("Enumeration:AmassBruteForce", true))
            args.Add("-brute");

        var wordlistPath = configuration["Enumeration:SubdomainWordlistPath"] ?? "/opt/nightmare/wordlists/subdomains.txt";
        if (!string.IsNullOrWhiteSpace(wordlistPath) && File.Exists(wordlistPath))
        {
            args.Add("-w");
            args.Add(wordlistPath);
        }
        else if (configuration.GetValue("Enumeration:AmassBruteForce", true))
        {
            logger.LogWarning(
                "Enumeration:SubdomainWordlistPath was not found ({WordlistPath}); amass will run without the bundled brute-force wordlist.",
                string.IsNullOrWhiteSpace(wordlistPath) ? "<not configured>" : wordlistPath);
        }

        args.Add("-d");
        args.Add(root);
        return args;
    }

    private async Task DiscoverCommonDnsNamesAsync(
        string root,
        ISet<string> discovered,
        CancellationToken cancellationToken)
    {
        var maxCandidates = Math.Clamp(configuration.GetValue("Enumeration:DnsFallbackMaxCandidates", 300), 1, 10_000);
        foreach (var prefix in ResolveFallbackPrefixes(maxCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = $"{prefix}.{root}".Trim().TrimEnd('.');
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (addrs.Length > 0)
                    discovered.Add(candidate);
            }
            catch
            {
                // Resolution failures are expected for most candidate names.
            }
        }
    }

    private IEnumerable<string> ResolveFallbackPrefixes(int maxCandidates)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in CandidatePrefixes)
        {
            if (yielded.Add(prefix))
                yield return prefix;
        }

        var wordlistPath = configuration["Enumeration:SubdomainWordlistPath"] ?? "/opt/nightmare/wordlists/subdomains.txt";
        if (string.IsNullOrWhiteSpace(wordlistPath) || !File.Exists(wordlistPath))
            yield break;

        foreach (var raw in File.ReadLines(wordlistPath))
        {
            if (yielded.Count >= maxCandidates)
                yield break;

            var prefix = NormalizePrefix(raw);
            if (!string.IsNullOrWhiteSpace(prefix) && yielded.Add(prefix))
                yield return prefix;
        }
    }

    private async Task RunToolAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string root,
        ISet<string> discovered,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

            logger.LogInformation(
                "Starting enumeration tool {Tool} for {Root} with timeout {TimeoutSeconds}s.",
                fileName,
                root,
                timeout.TotalSeconds);

            process = Process.Start(psi);
            if (process is null)
                return;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var beforeCount = discovered.Count;
            AddToolOutput(stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty, root, discovered);
            var addedCount = discovered.Count - beforeCount;
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            if (process.ExitCode == 0)
            {
                logger.LogInformation(
                    "Enumeration tool {Tool} finished for {Root}; added {AddedCount} scoped host(s).",
                    fileName,
                    root,
                    addedCount);
            }
            else if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogDebug(
                    "Enumeration tool {Tool} exited with {ExitCode}: {Stderr}",
                    fileName,
                    process.ExitCode,
                    Truncate(stderr.ReplaceLineEndings(" ").Trim(), 500));
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning("Enumeration tool {Tool} timed out after {TimeoutSeconds}s for {Root}.", fileName, timeout.TotalSeconds, root);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Enumeration tool {Tool} is not available; continuing with remaining enumeration methods.", fileName);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void AddToolOutput(string output, string root, ISet<string> discovered)
    {
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = NormalizeHostCandidate(raw);
            if (HostAllowed(host, root))
                discovered.Add(host);
        }
    }

    private string ResolveConfiguredPath(string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!;

    private TimeSpan ResolveTimeout(string key, int fallbackSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue(key, fallbackSeconds), 5, 3600));

    private static string NormalizeHostCandidate(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Tool output can contain sources, comments, or other columns. Keep the first plausible token.
        text = text.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        if (text.StartsWith("*."))
            text = text[2..];

        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "http:" + text;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            text = uri.Host;

        text = text.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0 || text.Length > 253 || text.Contains(' ') || text.Contains("..", StringComparison.Ordinal))
            return string.Empty;

        return text;
    }

    private static string NormalizePrefix(string value)
    {
        var prefix = value.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (prefix.Length == 0 || prefix.StartsWith('#') || prefix.Contains('/') || prefix.Contains(' ') || prefix.Contains("..", StringComparison.Ordinal))
            return string.Empty;

        return prefix;
    }

    private static bool HostAllowed(string host, string root) =>
        !string.IsNullOrWhiteSpace(host)
        && (host.Equals(root, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
