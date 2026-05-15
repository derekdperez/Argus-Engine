namespace ArgusEngine.CommandCenter.Updates.Api.Services;

public sealed class ComponentUpdaterOptions
{
    public bool Enabled { get; set; }

    public string RepositoryPath { get; set; } = "/workspace";

    public string ComposeFilePath { get; set; } = "/workspace/deployment/docker-compose.yml";

    public string GitRemote { get; set; } = "origin";

    public string MainBranch { get; set; } = "main";

    public bool RequireCleanWorkingTree { get; set; } = true;

    public int LogLimit { get; set; } = 200;

    public int CommandTimeoutSeconds { get; set; } = 900;
}

public sealed record ComponentStatusDto(
    string Key,
    string DisplayName,
    string ServiceName,
    string DeployedVersion,
    string DeployedRevision,
    string LatestVersion,
    string LatestRevision,
    string Location,
    string Status,
    bool IsRunning,
    bool IsHealthy,
    bool CanUpdate,
    string UpdateState,
    string Message);

public sealed record ComponentUpdateResultDto(
    Guid CorrelationId,
    string ComponentKey,
    bool Succeeded,
    string Message,
    IReadOnlyList<ComponentUpdateLogDto> Logs);

public sealed record ComponentUpdateLogDto(
    long Id,
    Guid CorrelationId,
    string ComponentKey,
    string Action,
    string Message,
    bool? Succeeded,
    string? Details,
    DateTimeOffset CreatedAtUtc);

internal sealed record ComponentDefinition(
    string Key,
    string DisplayName,
    string ServiceName,
    bool StopBeforeUpdate = true);

internal sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

internal sealed record ComposeTool(string FileName, string[] PrefixArguments);

