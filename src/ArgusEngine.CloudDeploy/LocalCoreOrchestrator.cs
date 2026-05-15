using CliWrap;
using CliWrap.EventStream;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CloudDeploy;

/// <summary>
/// Manages the local core services (Postgres, Redis, RabbitMQ, CommandCenter stack)
/// by shelling out to docker compose via CliWrap.
/// </summary>
internal sealed class LocalCoreOrchestrator(
    IOptions<GcpDeployOptions> options,
    ILogger<LocalCoreOrchestrator> logger)
{
    private readonly GcpDeployOptions _opts = options.Value;

    private string ComposeFilePath =>
        Path.IsPathRooted(_opts.CoreComposeFile)
            ? _opts.CoreComposeFile
            : Path.Combine(_opts.RepoRoot, _opts.CoreComposeFile);

    public async Task<CloudDeployResult> StartAsync(
        IProgress<DeployProgressEvent>? progress,
        CancellationToken ct)
    {
        if (!File.Exists(ComposeFilePath))
        {
            return CloudDeployResult.Fail(
                $"Core compose file not found: {ComposeFilePath}. " +
                "Ensure deployment/docker-compose.yml exists in the repo.");
        }

        progress?.Report(new(null, "Starting local core services via docker compose..."));
        logger.LogInformation("Starting local core services from {File}", ComposeFilePath);

        return await RunComposeAsync([
            "up",
            "-d",
            "--pull",
            "missing",
            "--scale",
            "gatekeeper=0",
            "--scale",
            "command-center-spider-dispatcher=0",
            "--scale",
            "worker-spider=0",
            "--scale",
            "worker-http-requester=0",
            "--scale",
            "worker-enum=0",
            "--scale",
            "worker-portscan=0",
            "--scale",
            "worker-highvalue=0",
            "--scale",
            "worker-techid=0",
        ], progress, ct);
    }

    public async Task<CloudDeployResult> StopAsync(
        IProgress<DeployProgressEvent>? progress,
        CancellationToken ct)
    {
        if (!File.Exists(ComposeFilePath))
            return CloudDeployResult.Fail($"Core compose file not found: {ComposeFilePath}");

        progress?.Report(new(null, "Stopping local core services..."));
        return await RunComposeAsync(["down"], progress, ct);
    }

    private async Task<CloudDeployResult> RunComposeAsync(
        string[] args,
        IProgress<DeployProgressEvent>? progress,
        CancellationToken ct)
    {
        var cmd = Cli.Wrap("docker")
            .WithArguments([
                "compose",
                "--file",
                ComposeFilePath,
                ..args,
            ])
            .WithWorkingDirectory(_opts.RepoRoot)
            .WithEnvironmentVariables(new Dictionary<string, string?>
            {
                ["ARGUS_WORKER_SPIDER_REPLICAS"] = "0",
                ["ARGUS_WORKER_HTTP_REQUESTER_REPLICAS"] = "0",
                ["ARGUS_WORKER_ENUM_REPLICAS"] = "0",
                ["ARGUS_WORKER_PORTSCAN_REPLICAS"] = "0",
                ["ARGUS_WORKER_HIGHVALUE_REPLICAS"] = "0",
                ["ARGUS_WORKER_TECHID_REPLICAS"] = "0",
            })
            .WithValidation(CommandResultValidation.None);

        var stderrLines = new List<string>();

        await foreach (var ev in cmd.ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o:
                    logger.LogDebug("[compose] {Line}", o.Text);
                    progress?.Report(new(null, o.Text));
                    break;

                case StandardErrorCommandEvent e:
                    // docker compose writes informational status lines to stderr too.
                    // Do not classify these as failures; rely only on the process exit code.
                    logger.LogDebug("[compose:err] {Line}", e.Text);
                    progress?.Report(new(null, e.Text));
                    stderrLines.Add(e.Text);
                    break;

                case ExitedCommandEvent ex when ex.ExitCode != 0:
                    var detail = stderrLines.Count > 0
                        ? string.Join(Environment.NewLine, stderrLines.TakeLast(5))
                        : $"exit code {ex.ExitCode}";

                    return CloudDeployResult.Fail(
                        $"docker compose failed (exit {ex.ExitCode}):{Environment.NewLine}{detail}");

                case ExitedCommandEvent:
                    break;
            }
        }

        return CloudDeployResult.Ok("Local core services operation completed.");
    }
}
