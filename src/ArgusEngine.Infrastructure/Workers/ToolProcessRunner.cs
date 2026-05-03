using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Workers;

public sealed class ToolProcessRunner(ILogger<ToolProcessRunner> logger)
{
    private static readonly Action<ILogger, string, int, string, Exception?> LogToolProcessFailed =
        LoggerMessage.Define<string, int, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogToolProcessFailed)),
            "Tool process failed. Binary={BinaryPath}, ExitCode={ExitCode}, Error={Error}");
    private static readonly Action<ILogger, string, double, Exception?> LogToolProcessTimedOut =
        LoggerMessage.Define<string, double>(
            LogLevel.Warning,
            new EventId(2, nameof(LogToolProcessTimedOut)),
            "Tool process timed out. Binary={BinaryPath}, TimeoutSeconds={TimeoutSeconds}");
    private static readonly Action<ILogger, string, string, Exception?> LogToolProcessCouldNotStart =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogToolProcessCouldNotStart)),
            "Tool process could not start. Binary={BinaryPath}, Error={Error}");
    private static readonly Action<ILogger, string, Exception?> LogUnexpectedToolProcessFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogUnexpectedToolProcessFailure)),
            "Unexpected tool process failure. Binary={BinaryPath}");

    public async Task<ToolProcessResult> RunAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
                startInfo.WorkingDirectory = workingDirectory;
            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);

            process = Process.Start(startInfo);
            if (process is null)
                return new ToolProcessResult { Success = false, Stderr = "process failed to start" };

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            var success = process.ExitCode == 0;
            if (!success)
            {
                LogToolProcessFailed(logger, binaryPath, process.ExitCode, Truncate(stderr, 2000), null);
            }

            return new ToolProcessResult
            {
                Success = success,
                ExitCode = process.ExitCode,
                Stdout = stdout,
                Stderr = stderr,
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            LogToolProcessTimedOut(logger, binaryPath, timeout.TotalSeconds, null);
            return new ToolProcessResult { Success = false, Stderr = $"timed out after {timeout.TotalSeconds:F0}s" };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogToolProcessCouldNotStart(logger, binaryPath, ex.Message, null);
            return new ToolProcessResult
            {
                Success = false,
                Exception = ex,
                Stderr = ex.Message,
            };
        }
        catch (Exception ex)
        {
            LogUnexpectedToolProcessFailure(logger, binaryPath, ex);
            return new ToolProcessResult
            {
                Success = false,
                Exception = ex,
                Stderr = ex.Message,
            };
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort.
        }
    }
}

public sealed class ToolProcessResult
{
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public Exception? Exception { get; init; }
}
