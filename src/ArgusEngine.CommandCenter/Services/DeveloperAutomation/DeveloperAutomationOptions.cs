namespace ArgusEngine.CommandCenter.Services.DeveloperAutomation;

public sealed class DeveloperAutomationOptions
{
    public bool Enabled { get; set; }

    public string RepositoryOwner { get; set; } = "derekdperez";

    public string RepositoryName { get; set; } = "Argus-Engine";

    public string WorkflowFile { get; set; } = "ai-code-automation.yml";

    public string DefaultBranch { get; set; } = "main";

    public string DefaultDeployEnvironment { get; set; } = "staging";

    public string? GitHubToken { get; set; }

    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com/";
}
