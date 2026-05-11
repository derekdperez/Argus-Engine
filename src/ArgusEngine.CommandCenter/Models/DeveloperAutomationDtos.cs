namespace ArgusEngine.CommandCenter.Models;

public sealed class DeveloperAutomationStatusDto
{
    public bool Enabled { get; init; }

    public required string Repository { get; init; }

    public required string Workflow { get; init; }

    public required string DefaultBranch { get; init; }

    public bool TokenConfigured { get; init; }
}

public sealed class DeveloperAutomationRequestDto
{
    public string? Title { get; init; }

    public required string Description { get; init; }

    public string? Prompt { get; init; }

    public string? BranchName { get; init; }

    public string? BaseBranch { get; init; }

    public string? DeployEnvironment { get; init; }

    public bool AutoDeploy { get; init; } = true;
}

public sealed class DeveloperAutomationResponseDto
{
    public bool Queued { get; init; }

    public required string Message { get; init; }

    public string? WorkflowUrl { get; init; }

    public string? BranchName { get; init; }

    public string? CorrelationId { get; init; }
}
