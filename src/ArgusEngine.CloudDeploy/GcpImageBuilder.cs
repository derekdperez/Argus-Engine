using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CloudDeploy;

/// <summary>
/// Builds worker Docker images and pushes them to Google Artifact Registry
/// by shelling out to <c>docker</c> and <c>gcloud</c> via CliWrap.
/// </summary>
internal sealed class GcpImageBuilder(
    IOptions<GcpDeployOptions>  options,
    ILogger<GcpImageBuilder>    logger)
{
    private readonly GcpDeployOptions _opts = options.Value;

    public string GetImageUri(WorkerType worker) =>
        $"{_opts.ResolvedImagePrefix}/worker-{worker.ToSlug()}:{_opts.ImageTag}";

    // ── GAR setup ─────────────────────────────────────────────────────────────

    public async Task EnsureGarRepositoryAsync(
        IProgress<DeployProgressEvent>? progress,
        CancellationToken ct)
    {
        progress?.Report(new(null, "Enabling GCP APIs..."));

        await Cli.Wrap("gcloud")
            .WithArguments([
                "services", "enable",
                "artifactregistry.googleapis.com",
                "run.googleapis.com",
                "--project", _opts.ProjectId,
                "--quiet",
            ])
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .ExecuteAsync(ct);

        // Check if repo exists
        var check = await Cli.Wrap("gcloud")
            .WithArguments([
                "artifacts", "repositories", "describe", _opts.GarRepository,
                "--location", _opts.Region,
                "--project", _opts.ProjectId,
                "--format", "value(name)",
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (check.ExitCode == 0)
        {
            progress?.Report(new(null, $"GAR repository '{_opts.GarRepository}' already exists."));
            return;
        }

        progress?.Report(new(null, $"Creating GAR repository '{_opts.GarRepository}'..."));

        await Cli.Wrap("gcloud")
            .WithArguments([
                "artifacts", "repositories", "create", _opts.GarRepository,
                "--repository-format", "docker",
                "--location", _opts.Region,
                "--project", _opts.ProjectId,
                "--description", "Argus Engine worker images",
                "--quiet",
            ])
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .ExecuteAsync(ct);

        // Configure Docker credential helper
        await Cli.Wrap("gcloud")
            .WithArguments([
                "auth", "configure-docker",
                $"{_opts.Region}-docker.pkg.dev",
                "--quiet",
            ])
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .ExecuteAsync(ct);

        progress?.Report(new(null, $"GAR repository ready at {_opts.ResolvedImagePrefix}"));
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    public async Task<CloudDeployResult> BuildImageAsync(
        WorkerType                      worker,
        IProgress<DeployProgressEvent>? progress,
        CancellationToken               ct)
    {
        var imageUri = GetImageUri(worker);
        var projectDir = worker.ToProjectDir();
        var appDll = worker.ToAppDll();
        var dockerfileName = worker == WorkerType.Enumeration ? "Dockerfile.worker-enum" : "Dockerfile.worker";
        var dockerfilePath = Path.Combine(_opts.RepoRoot, "deploy", dockerfileName);

        if (!File.Exists(dockerfilePath))
            return CloudDeployResult.Fail(
                $"Dockerfile.worker not found at {dockerfilePath}. " +
                "Ensure deploy/Dockerfile.worker exists in the repo.");

        progress?.Report(new(worker, $"Building image: {imageUri}"));
        logger.LogInformation("Building {Worker} → {Image}", worker, imageUri);

        var stdErr = new System.Text.StringBuilder();

        var result = await Cli.Wrap("docker")
            .WithArguments([
                "build",
                "--file", dockerfilePath,
                "--build-arg", $"PROJECT_DIR={projectDir}",
                "--build-arg", $"APP_DLL={appDll}",
                "--tag", imageUri,
                _opts.RepoRoot,
            ])
            .WithWorkingDirectory(_opts.RepoRoot)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0)
        {
            var error = stdErr.ToString();
            logger.LogError("Build failed for {Worker}: {Error}", worker, error);
            return CloudDeployResult.Fail($"docker build failed for {worker.ToSlug()}: {error[..Math.Min(500, error.Length)]}");
        }

        progress?.Report(new(worker, $"Build complete: {imageUri}"));
        return CloudDeployResult.Ok($"Built {imageUri}");
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    public async Task<CloudDeployResult> PushImageAsync(
        WorkerType                      worker,
        IProgress<DeployProgressEvent>? progress,
        CancellationToken               ct)
    {
        var imageUri = GetImageUri(worker);
        progress?.Report(new(worker, $"Pushing image to GAR: {imageUri}"));
        logger.LogInformation("Pushing {Image}", imageUri);

        var stdErr = new System.Text.StringBuilder();

        var result = await Cli.Wrap("docker")
            .WithArguments(["push", imageUri])
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        if (result.ExitCode != 0)
        {
            var error = stdErr.ToString();
            logger.LogError("Push failed for {Worker}: {Error}", worker, error);
            return CloudDeployResult.Fail($"docker push failed for {worker.ToSlug()}: {error[..Math.Min(500, error.Length)]}");
        }

        progress?.Report(new(worker, $"Pushed: {imageUri}"));
        return CloudDeployResult.Ok($"Pushed {imageUri}", imageUri);
    }

    // ── Preflight ─────────────────────────────────────────────────────────────

    public async Task<bool> IsDockerAvailableAsync(CancellationToken ct)
    {
        var r = await Cli.Wrap("docker").WithArguments("info")
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        return r.ExitCode == 0;
    }

    public async Task<bool> IsGcloudAuthenticatedAsync(CancellationToken ct)
    {
        var r = await Cli.Wrap("gcloud")
            .WithArguments(["auth", "list", "--filter=status:ACTIVE", "--format=value(account)"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return r.ExitCode == 0 && r.StandardOutput.Contains("@");
    }
}
