using CliWrap;
using CliWrap.EventStream;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CloudDeploy;

/// <summary>
/// Manages the local core services (Postgres, Redis, RabbitMQ, CommandCenter stack)
/// by shelling out to <c>docker compose</c> via CliWrap.
/// </summary>
internal sealed class LocalCoreOrchestrator(
    IOptions<GcpDeployOptions>      options,
    ILogger<LocalCoreOrchestrator>  logger)
{
    private readonly GcpDeployOptions _opts = options.Value;

    private string ComposeFilePath =>
        Path.IsPathRooted(_opts.CoreComposeFile)
            ? _opts.CoreComposeFile
            : Path.Combine(_opts.RepoRoot, _opts.CoreComposeFile);

    public async Task<CloudDeployResult> StartAsync(
        IProgress<DeployProgressEvent>? progress,
        CancellationToken               ct)
    {
        if (!File.Exists(ComposeFilePath))
            return CloudDeployResult.Fail(
                $"Core compose file not found: {ComposeFilePath}. " +
                "Ensure deploy/docker-compose.core.yml exists in the repo.");

        progress?.Report(new(null, "Starting local core services via docker compose..."));
        logger.LogInformation("Starting local core services from {File}", ComposeFilePath);

        return await RunComposeAsync(["up", "-d", "--pull", "missing"], progress, ct);
    }

    public async Task<CloudDeployResult> StopAsync(
        IProgress<DeployProgressEvent>? progress,
        CancellationToken               ct)
    {
        if (!File.Exists(ComposeFilePath))
            return CloudDeployResult.Fail($"Core compose file not found: {ComposeFilePath}");

        progress?.Report(new(null, "Stopping local core services..."));
        return await RunComposeAsync(["down"], progress, ct);
    }

    private async Task<CloudDeployResult> RunComposeAsync(
        string[]                        args,
        IProgress<DeployProgressEvent>? progress,
        CancellationToken               ct)
    {
        var cmd = Cli.Wrap("docker")
            .WithArguments([
                "compose",
                "--file", ComposeFilePath,
                ..args,
            ])
            .WithWorkingDirectory(_opts.RepoRoot)
            .WithValidation(CommandResultValidation.None);

        var errors = new List<string>();

        await foreach (var ev in cmd.ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o:
                    logger.LogDebug("[compose] {Line}", o.Text);
                    progress?.Report(new(null, o.Text));
                    break;
                case StandardErrorCommandEvent e:
                    // docker compose writes normal status lines to stderr too
                    logger.LogDebug("[compose:err] {Line}", e.Text);
                    progress?.Report(new(null, e.Text));
                    if (e.Text.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        e.Text.Contains("error", StringComparison.OrdinalIgnoreCase))
                        errors.Add(e.Text);
                    break;
                case ExitedCommandEvent ex:
                    if (ex.ExitCode != 0)
                    {
                        var msg = errors.Count > 0
                            ? string.Join("; ", errors)
                            : $"docker compose exited with code {ex.ExitCode}";
                        return CloudDeployResult.Fail(msg);
                    }
                    break;
            }
        }

        return CloudDeployResult.Ok("Local core services operation completed.");
    }
}
