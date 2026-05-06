using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;
using Npgsql;

namespace ArgusEngine.CommandCenter.Services.Updates;

public interface IComponentUpdateService
{
    Task<IReadOnlyList<ComponentStatusDto>> GetComponentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComponentUpdateLogDto>> GetLogsAsync(int? limit = null, CancellationToken cancellationToken = default);

    Task<ComponentUpdateResultDto> UpdateComponentAsync(string componentKey, CancellationToken cancellationToken = default);
}

public sealed class ComponentUpdateService : IComponentUpdateService
{
    private static readonly IReadOnlyList<ComponentDefinition> Components =
    [
        new("command-center", "Command Center", "command-center", StopBeforeUpdate: false),
        new("gatekeeper", "Gatekeeper", "gatekeeper"),
        new("worker-spider", "Spider Worker", "worker-spider"),
        new("worker-enum", "Enumeration Worker", "worker-enum"),
        new("worker-portscan", "Port Scan Worker", "worker-portscan"),
        new("worker-highvalue", "High Value Worker", "worker-highvalue"),
        new("worker-techid", "Technology Identification Worker", "worker-techid")
    ];

    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    private readonly ComponentUpdaterOptions _options;
    private readonly ILogger<ComponentUpdateService> _logger;
    private readonly string? _connectionString;

    private ComposeTool? _composeTool;

    public ComponentUpdateService(
        IOptions<ComponentUpdaterOptions> options,
        IConfiguration configuration,
        ILogger<ComponentUpdateService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("Postgres");
    }

    public async Task<IReadOnlyList<ComponentStatusDto>> GetComponentsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLogTableAsync(cancellationToken);

        var readiness = await GetReadinessMessageAsync(cancellationToken);
        var latestRevision = "unavailable";
        var latestVersion = "unavailable";

        if (readiness is null)
        {
            var fetch = await FetchLatestAsync(cancellationToken);
            if (!fetch.Succeeded)
            {
                readiness = $"Unable to fetch latest GitHub main: {Trim(fetch.CombinedOutput)}";
            }

            latestRevision = await GetGitValueAsync(["rev-parse", "--short=12", $"{_options.GitRemote}/{_options.MainBranch}"], cancellationToken);
            latestVersion = await GetGitValueAsync(["show", $"{_options.GitRemote}/{_options.MainBranch}:VERSION"], cancellationToken);
        }

        var result = new List<ComponentStatusDto>(Components.Count);
        foreach (var component in Components)
        {
            var runtime = await InspectComponentAsync(component, cancellationToken);

            var canUpdate =
                _options.Enabled &&
                readiness is null &&
                runtime.IsRunning &&
                (IsDifferent(runtime.DeployedRevision, latestRevision) || IsDifferent(runtime.DeployedVersion, latestVersion));

            var updateState = readiness is not null
                ? "Not configured"
                : canUpdate
                    ? "Update available"
                    : "Current or unavailable";

            result.Add(new ComponentStatusDto(
                component.Key,
                component.DisplayName,
                component.ServiceName,
                runtime.DeployedVersion,
                runtime.DeployedRevision,
                latestVersion,
                latestRevision,
                runtime.Location,
                runtime.Status,
                runtime.IsRunning,
                runtime.IsHealthy,
                canUpdate,
                updateState,
                readiness ?? runtime.Message));
        }

        return result;
    }

    public async Task<IReadOnlyList<ComponentUpdateLogDto>> GetLogsAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureLogTableAsync(cancellationToken);

        var rowLimit = Math.Clamp(limit ?? _options.LogLimit, 1, 1000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, correlation_id, component_key, action, message, succeeded, details, created_at_utc
            FROM component_update_logs
            ORDER BY created_at_utc DESC, id DESC
            LIMIT @limit;
            """,
            connection);

        command.Parameters.AddWithValue("limit", rowLimit);

        var rows = new List<ComponentUpdateLogDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ComponentUpdateLogDto(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return rows;
    }

    public async Task<ComponentUpdateResultDto> UpdateComponentAsync(
        string componentKey,
        CancellationToken cancellationToken = default)
    {
        var component = Components.FirstOrDefault(c => string.Equals(c.Key, componentKey, StringComparison.OrdinalIgnoreCase));
        if (component is null)
        {
            return new ComponentUpdateResultDto(
                Guid.NewGuid(),
                componentKey,
                false,
                $"Unknown component '{componentKey}'.",
                []);
        }

        var correlationId = Guid.NewGuid();
        var logs = new List<ComponentUpdateLogDto>();

        async Task AddStepAsync(string action, string message, bool? succeeded = null, string? details = null)
        {
            var log = await SaveLogAsync(correlationId, component.Key, action, message, succeeded, details, cancellationToken);
            logs.Add(log);
        }

        if (!await UpdateLock.WaitAsync(0, cancellationToken))
        {
            await AddStepAsync("Rejected", "Another component update is already running.", false);
            return new ComponentUpdateResultDto(correlationId, component.Key, false, "Another component update is already running.", logs);
        }

        try
        {
            await EnsureLogTableAsync(cancellationToken);
            await AddStepAsync("Queued", $"Starting update for {component.DisplayName}.", true);

            var readiness = await GetReadinessMessageAsync(cancellationToken);
            if (readiness is not null)
            {
                await AddStepAsync("Preflight", readiness, false);
                return new ComponentUpdateResultDto(correlationId, component.Key, false, readiness, logs);
            }

            var clean = await EnsureCleanWorkingTreeAsync(cancellationToken);
            if (!clean.Succeeded)
            {
                await AddStepAsync("Preflight", "Repository working tree is not clean.", false, clean.CombinedOutput);
                return new ComponentUpdateResultDto(correlationId, component.Key, false, "Repository working tree is not clean.", logs);
            }

            await AddStepAsync("Fetch", $"Fetching {_options.GitRemote}/{_options.MainBranch} from GitHub.", null);
            var fetch = await FetchLatestAsync(cancellationToken);
            await AddStepAsync("Fetch", fetch.Succeeded ? "Fetch completed." : "Fetch failed.", fetch.Succeeded, fetch.CombinedOutput);
            if (!fetch.Succeeded)
            {
                return new ComponentUpdateResultDto(correlationId, component.Key, false, "Fetch failed.", logs);
            }

            if (component.StopBeforeUpdate)
            {
                await AddStepAsync("Stop", $"Stopping {component.ServiceName}.", null);
                var stop = await RunComposeAsync(["stop", component.ServiceName], cancellationToken);
                await AddStepAsync("Stop", stop.Succeeded ? "Component stopped." : "Stop command failed.", stop.Succeeded, stop.CombinedOutput);
                if (!stop.Succeeded)
                {
                    return new ComponentUpdateResultDto(correlationId, component.Key, false, "Stop command failed.", logs);
                }
            }
            else
            {
                await AddStepAsync(
                    "Stop",
                    "Command Center self-update will redeploy in place; the browser request may disconnect while the container is recreated.",
                    true);
            }

            await AddStepAsync("Pull", $"Fast-forwarding local main from {_options.GitRemote}/{_options.MainBranch}.", null);
            var checkout = await RunGitAsync(["checkout", _options.MainBranch], cancellationToken);
            await AddStepAsync("Checkout", checkout.Succeeded ? "Checked out main branch." : "Checkout failed.", checkout.Succeeded, checkout.CombinedOutput);
            if (!checkout.Succeeded)
            {
                return new ComponentUpdateResultDto(correlationId, component.Key, false, "Checkout failed.", logs);
            }

            var pull = await RunGitAsync(["pull", "--ff-only", _options.GitRemote, _options.MainBranch], cancellationToken);
            await AddStepAsync("Pull", pull.Succeeded ? "Repository updated to latest main." : "Pull failed.", pull.Succeeded, pull.CombinedOutput);
            if (!pull.Succeeded)
            {
                return new ComponentUpdateResultDto(correlationId, component.Key, false, "Pull failed.", logs);
            }

            await AddStepAsync("Build", $"Building {component.ServiceName}.", null);
            var build = await RunComposeAsync(["build", component.ServiceName], cancellationToken);
            await AddStepAsync("Build", build.Succeeded ? "Build completed." : "Build failed.", build.Succeeded, build.CombinedOutput);
            if (!build.Succeeded)
            {
                return new ComponentUpdateResultDto(correlationId, component.Key, false, "Build failed.", logs);
            }

            await AddStepAsync("Deploy", $"Redeploying {component.ServiceName}.", null);
            var deploy = await RunComposeAsync(["up", "-d", "--no-deps", component.ServiceName], cancellationToken);
            await AddStepAsync("Deploy", deploy.Succeeded ? "Redeploy completed." : "Redeploy failed.", deploy.Succeeded, deploy.CombinedOutput);
            if (!deploy.Succeeded)
            {
                return new ComponentUpdateResultDto(correlationId, component.Key, false, "Redeploy failed.", logs);
            }

            await AddStepAsync("Complete", $"{component.DisplayName} update completed.", true);
            return new ComponentUpdateResultDto(correlationId, component.Key, true, $"{component.DisplayName} update completed.", logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Component update failed for {ComponentKey}.", component.Key);
            await AddStepAsync("Exception", ex.Message, false, ex.ToString());
            return new ComponentUpdateResultDto(correlationId, component.Key, false, ex.Message, logs);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private async Task<string?> GetReadinessMessageAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return "Component updater is disabled. Set Argus:ComponentUpdater:Enabled=true to enable it.";
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return "Postgres connection string is missing; update logs cannot be saved.";
        }

        if (!Directory.Exists(_options.RepositoryPath))
        {
            return $"Repository path '{_options.RepositoryPath}' is not mounted or does not exist.";
        }

        if (!Directory.Exists(Path.Combine(_options.RepositoryPath, ".git")))
        {
            return $"Repository path '{_options.RepositoryPath}' is not a Git checkout.";
        }

        if (!File.Exists(_options.ComposeFilePath))
        {
            return $"Compose file '{_options.ComposeFilePath}' does not exist.";
        }

        var git = await RunAsync("git", ["--version"], cancellationToken: cancellationToken);
        if (!git.Succeeded)
        {
            return "git is not available in the Command Center container.";
        }

        var docker = await RunAsync("docker", ["--version"], cancellationToken: cancellationToken);
        if (!docker.Succeeded)
        {
            return "docker CLI is not available in the Command Center container.";
        }

        var compose = await ResolveComposeToolAsync(cancellationToken);
        if (compose is null)
        {
            return "Docker Compose is not available. Install Docker Compose v2 or docker-compose in the Command Center container.";
        }

        return null;
    }

    private async Task<RuntimeComponentState> InspectComponentAsync(ComponentDefinition component, CancellationToken cancellationToken)
    {
        var readiness = await GetReadinessMessageAsync(cancellationToken);
        if (readiness is not null)
        {
            return new RuntimeComponentState(
                "unknown",
                "unknown",
                Dns.GetHostName(),
                "Not configured",
                IsRunning: false,
                IsHealthy: false,
                readiness);
        }

        var ps = await RunComposeAsync(["ps", "-q", component.ServiceName], cancellationToken);
        if (!ps.Succeeded)
        {
            return new RuntimeComponentState(
                "unknown",
                "unknown",
                Dns.GetHostName(),
                "Docker status unavailable",
                IsRunning: false,
                IsHealthy: false,
                Trim(ps.CombinedOutput));
        }

        var containerIds = ps.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (containerIds.Length == 0)
        {
            return new RuntimeComponentState(
                "not deployed",
                "unknown",
                $"{Dns.GetHostName()} / {component.ServiceName}",
                "Not running",
                IsRunning: false,
                IsHealthy: false,
                "No container is running for this compose service.");
        }

        var containers = new List<ContainerRuntimeInfo>();
        foreach (var id in containerIds)
        {
            var inspect = await RunAsync("docker", ["inspect", id], cancellationToken: cancellationToken);
            if (!inspect.Succeeded)
            {
                continue;
            }

            containers.AddRange(ParseInspectOutput(inspect.StandardOutput));
        }

        if (containers.Count == 0)
        {
            return new RuntimeComponentState(
                "unknown",
                "unknown",
                $"{Dns.GetHostName()} / {component.ServiceName}",
                "Inspect failed",
                IsRunning: false,
                IsHealthy: false,
                "Docker returned container ids but inspect did not return usable metadata.");
        }

        var versions = containers.Select(c => c.Version).Where(IsKnown).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var revisions = containers.Select(c => c.Revision).Where(IsKnown).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var locations = containers.Select(c => $"{Dns.GetHostName()}:{c.Name}").Distinct(StringComparer.OrdinalIgnoreCase);
        var running = containers.Count(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase));
        var healthy = containers.Count(c => string.Equals(c.Health, "healthy", StringComparison.OrdinalIgnoreCase));
        var hasHealth = containers.Any(c => IsKnown(c.Health));

        var status = hasHealth
            ? $"{running}/{containers.Count} running, {healthy}/{containers.Count} healthy"
            : $"{running}/{containers.Count} running";

        return new RuntimeComponentState(
            versions.Length == 0 ? "unknown" : string.Join(", ", versions),
            revisions.Length == 0 ? "unknown" : string.Join(", ", revisions.Select(ShortenRevision)),
            string.Join(", ", locations),
            status,
            running == containers.Count,
            !hasHealth || healthy == containers.Count,
            containers.Count == containerIds.Length ? "Runtime metadata loaded." : "Some containers could not be inspected.");
    }

    private async Task<CommandResult> EnsureCleanWorkingTreeAsync(CancellationToken cancellationToken)
    {
        if (!_options.RequireCleanWorkingTree)
        {
            return new CommandResult(0, "Clean working tree check disabled by configuration.", string.Empty);
        }

        var status = await RunGitAsync(["status", "--porcelain"], cancellationToken);
        if (!status.Succeeded)
        {
            return status;
        }

        return string.IsNullOrWhiteSpace(status.StandardOutput)
            ? new CommandResult(0, "Working tree is clean.", string.Empty)
            : new CommandResult(1, status.StandardOutput, "Refusing update because local repository has uncommitted/untracked changes.");
    }

    private Task<CommandResult> FetchLatestAsync(CancellationToken cancellationToken) =>
        RunGitAsync(["fetch", "--prune", _options.GitRemote, _options.MainBranch], cancellationToken);

    private async Task<string> GetGitValueAsync(string[] args, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(args, cancellationToken);
        return result.Succeeded ? Trim(result.StandardOutput) : "unavailable";
    }

    private Task<CommandResult> RunGitAsync(string[] args, CancellationToken cancellationToken) =>
        RunAsync("git", ["-C", _options.RepositoryPath, .. args], cancellationToken: cancellationToken);

    private async Task<CommandResult> RunComposeAsync(string[] args, CancellationToken cancellationToken)
    {
        var tool = await ResolveComposeToolAsync(cancellationToken);
        if (tool is null)
        {
            return new CommandResult(127, string.Empty, "Docker Compose command is unavailable.");
        }

        var composeArgs = tool.PrefixArguments
            .Concat(["-f", _options.ComposeFilePath])
            .Concat(args)
            .ToArray();

        return await RunAsync(
            tool.FileName,
            composeArgs,
            workingDirectory: _options.RepositoryPath,
            timeoutSeconds: _options.CommandTimeoutSeconds,
            cancellationToken: cancellationToken);
    }

    private async Task<ComposeTool?> ResolveComposeToolAsync(CancellationToken cancellationToken)
    {
        if (_composeTool is not null)
        {
            return _composeTool;
        }

        var dockerCompose = await RunAsync("docker", ["compose", "version"], cancellationToken: cancellationToken);
        if (dockerCompose.Succeeded)
        {
            _composeTool = new ComposeTool("docker", ["compose"]);
            return _composeTool;
        }

        var legacyCompose = await RunAsync("docker-compose", ["version"], cancellationToken: cancellationToken);
        if (legacyCompose.Succeeded)
        {
            _composeTool = new ComposeTool("docker-compose", []);
            return _composeTool;
        }

        return null;
    }

    private async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = timeoutSeconds is null
            ? null
            : new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value));

        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new CommandResult(127, string.Empty, $"Failed to start '{fileName}'.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(linkedCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return new CommandResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            return new CommandResult(124, string.Empty, $"Command timed out after {timeoutSeconds} seconds: {fileName} {string.Join(' ', arguments)}");
        }
        catch (Exception ex)
        {
            return new CommandResult(127, string.Empty, ex.Message);
        }
    }

    private async Task EnsureLogTableAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS component_update_logs (
                id bigserial PRIMARY KEY,
                correlation_id uuid NOT NULL,
                component_key text NOT NULL,
                action text NOT NULL,
                message text NOT NULL,
                succeeded boolean NULL,
                details text NULL,
                created_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_component_update_logs_created_at_utc
                ON component_update_logs (created_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_component_update_logs_correlation_id
                ON component_update_logs (correlation_id);
            """,
            connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ComponentUpdateLogDto> SaveLogAsync(
        Guid correlationId,
        string componentKey,
        string action,
        string message,
        bool? succeeded,
        string? details,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return new ComponentUpdateLogDto(
                0,
                correlationId,
                componentKey,
                action,
                message,
                succeeded,
                details,
                DateTimeOffset.UtcNow);
        }

        await EnsureLogTableAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO component_update_logs (correlation_id, component_key, action, message, succeeded, details)
            VALUES (@correlation_id, @component_key, @action, @message, @succeeded, @details)
            RETURNING id, created_at_utc;
            """,
            connection);

        command.Parameters.AddWithValue("correlation_id", correlationId);
        command.Parameters.AddWithValue("component_key", componentKey);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.AddWithValue("succeeded", succeeded is null ? DBNull.Value : succeeded.Value);
        command.Parameters.AddWithValue("details", string.IsNullOrWhiteSpace(details) ? DBNull.Value : details);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new ComponentUpdateLogDto(
            reader.GetInt64(0),
            correlationId,
            componentKey,
            action,
            message,
            succeeded,
            details,
            reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static IReadOnlyList<ContainerRuntimeInfo> ParseInspectOutput(string json)
    {
        var containers = new List<ContainerRuntimeInfo>();

        using var document = JsonDocument.Parse(json);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = element.TryGetProperty("Name", out var nameElement)
                ? nameElement.GetString()?.TrimStart('/') ?? "unknown"
                : "unknown";

            var state = "unknown";
            var health = "unknown";

            if (element.TryGetProperty("State", out var stateElement))
            {
                if (stateElement.TryGetProperty("Status", out var statusElement))
                {
                    state = statusElement.GetString() ?? "unknown";
                }

                if (stateElement.TryGetProperty("Health", out var healthElement) &&
                    healthElement.TryGetProperty("Status", out var healthStatusElement))
                {
                    health = healthStatusElement.GetString() ?? "unknown";
                }
            }

            var labels = element.TryGetProperty("Config", out var configElement) &&
                         configElement.TryGetProperty("Labels", out var labelsElement)
                ? labelsElement
                : default;

            var version = GetLabel(labels, "org.opencontainers.image.version");
            var revision = GetLabel(labels, "org.opencontainers.image.revision");

            containers.Add(new ContainerRuntimeInfo(name, state, health, version, revision));
        }

        return containers;
    }

    private static string GetLabel(JsonElement labels, string key)
    {
        if (labels.ValueKind != JsonValueKind.Object)
        {
            return "unknown";
        }

        return labels.TryGetProperty(key, out var value)
            ? value.GetString() ?? "unknown"
            : "unknown";
    }

    private static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase);

    private static bool IsDifferent(string deployed, string latest)
    {
        if (!IsKnown(deployed) || !IsKnown(latest))
        {
            return false;
        }

        return !deployed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => string.Equals(ShortenRevision(value), ShortenRevision(latest), StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(value, latest, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortenRevision(string revision) =>
        revision.Length > 12 ? revision[..12] : revision;

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 4000 ? trimmed : trimmed[..4000] + Environment.NewLine + "... truncated ...";
    }

    private sealed record RuntimeComponentState(
        string DeployedVersion,
        string DeployedRevision,
        string Location,
        string Status,
        bool IsRunning,
        bool IsHealthy,
        string Message);

    private sealed record ContainerRuntimeInfo(
        string Name,
        string State,
        string Health,
        string Version,
        string Revision);
}
