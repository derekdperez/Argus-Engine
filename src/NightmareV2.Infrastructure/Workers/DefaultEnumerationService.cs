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
        var root = NormalizeRoot(target.RootDomain);
        if (string.IsNullOrWhiteSpace(root))
            return [];

        if (configuration.GetValue("Enumeration:UseSubfinder", true))
        {
            await RunToolAsync(
                    "subfinder",
                    ["-silent", "-d", root],
                    root,
                    discovered,
                    ResolveTimeout("Enumeration:SubfinderTimeoutSeconds", 180),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration.GetValue("Enumeration:UseAmass", true))
        {
            await RunToolAsync(
                    "amass",
                    ["enum", "-d", root],
                    root,
                    discovered,
                    ResolveTimeout("Enumeration:AmassTimeoutSeconds", 600),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration.GetValue("Enumeration:UseDnsFallback", true))
        {
            await DiscoverCommonDnsNamesAsync(root, discovered, cancellationToken).ConfigureAwait(false);
        }

        return discovered.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task DiscoverCommonDnsNamesAsync(
        string root,
        ISet<string> discovered,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in CandidatePrefixes)
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

            process = Process.Start(psi);
            if (process is null)
                return;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            AddToolOutput(stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "", root, discovered);
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
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
            logger.LogDebug("Enumeration tool {Tool} is not available; DNS fallback will still run.", fileName);
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
            var host = NormalizeRoot(raw);
            if (HostAllowed(host, root))
                discovered.Add(host);
        }
    }

    private TimeSpan ResolveTimeout(string key, int fallbackSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue(key, fallbackSeconds), 5, 3600));

    private static string NormalizeRoot(string value) =>
        value.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool HostAllowed(string host, string root) =>
        !string.IsNullOrWhiteSpace(host)
        && (host.Equals(root, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
