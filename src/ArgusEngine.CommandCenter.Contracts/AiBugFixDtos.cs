namespace ArgusEngine.CommandCenter.Contracts;

/// <summary>Status constants for an AI bug-fix run state machine.</summary>
public static class AiBugFixRunStatus
{
    public const string CollectingErrors    = "CollectingErrors";
    public const string DispatchingWorkflow = "DispatchingWorkflow";
    public const string AiGeneratingPatch   = "AiGeneratingPatch";
    public const string ValidatingPatch     = "ValidatingPatch";
    public const string PullRequestOpen     = "PullRequestOpen";
    public const string WaitingForApproval  = "WaitingForApproval";
    public const string MergeReady          = "MergeReady";
    public const string Merging             = "Merging";
    public const string Deploying           = "Deploying";
    public const string SmokeTesting        = "SmokeTesting";
    public const string Deployed            = "Deployed";
    public const string Failed              = "Failed";
    public const string Canceled            = "Canceled";

    public static bool IsTerminal(string status) =>
        status is Deployed or Failed or Canceled;

    public static bool IsActive(string status) =>
        !IsTerminal(status);
}

public sealed record CreateAiBugFixRunRequest(
    string? Scope,
    IReadOnlyList<string>? Components,
    IReadOnlyList<Guid>? ErrorIds,
    bool IncludeDockerLogs = true,
    bool IncludeStructuredErrors = true);

public sealed record CreateAiBugFixRunResponse(
    Guid RunId,
    string Status,
    string? GitHubWorkflowUrl,
    string Message);

public sealed record AiBugFixRunDto(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Status,
    string? StatusMessage,
    int ErrorCount,
    IReadOnlyList<string> ComponentScope,
    string? GitHubBranch,
    int? GitHubPrNumber,
    string? GitHubPrUrl,
    long? GitHubWorkflowRunId,
    string? GitHubWorkflowUrl,
    string? DeploymentUrl,
    DateTimeOffset? DeploymentCompletedAtUtc,
    string? FailureDetail);

public sealed record AiBugFixWorkflowCallbackRequest(
    Guid RunId,
    string Status,
    string? StatusMessage,
    string? Branch,
    int? PullRequestNumber,
    string? PullRequestUrl,
    long? WorkflowRunId,
    string? WorkflowUrl,
    string? MergeSha,
    string? DeploymentRunId,
    string? DeploymentUrl,
    string? FailureDetail,
    string? SmokeTestResultJson);

public sealed record AiBugFixPromptBundleDto(
    Guid RunId,
    string PromptText,
    string GitHubOwner,
    string GitHubRepo,
    string CallbackBaseUrl,
    int MaxFiles,
    long MaxFileSizeBytes,
    IReadOnlyList<string> AllowedPathPrefixes);
