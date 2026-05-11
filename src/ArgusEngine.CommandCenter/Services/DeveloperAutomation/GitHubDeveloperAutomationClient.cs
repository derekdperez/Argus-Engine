using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ArgusEngine.CommandCenter.Models;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CommandCenter.Services.DeveloperAutomation;

public sealed class GitHubDeveloperAutomationClient
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<DeveloperAutomationOptions> _options;
    private readonly ILogger<GitHubDeveloperAutomationClient> _logger;

    public GitHubDeveloperAutomationClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<DeveloperAutomationOptions> options,
        ILogger<GitHubDeveloperAutomationClient> logger)
    {
        _http = httpClientFactory.CreateClient("developer-automation-github");
        _options = options;
        _logger = logger;
    }

    public DeveloperAutomationStatusDto GetStatus()
    {
        var options = _options.CurrentValue;
        return new DeveloperAutomationStatusDto
        {
            Enabled = options.Enabled,
            Repository = $"{options.RepositoryOwner}/{options.RepositoryName}",
            Workflow = options.WorkflowFile,
            DefaultBranch = options.DefaultBranch,
            TokenConfigured = !string.IsNullOrWhiteSpace(options.GitHubToken),
        };
    }

    public async Task<DeveloperAutomationResponseDto> QueueAsync(
        string mode,
        DeveloperAutomationRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(mode, "bugfix", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "feature", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Automation mode must be either bugfix or feature.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new ArgumentException("Description is required.", nameof(request));
        }

        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Developer automation is disabled. Set DeveloperAutomation:Enabled to true.");
        }

        if (string.IsNullOrWhiteSpace(options.GitHubToken))
        {
            throw new InvalidOperationException("DeveloperAutomation:GitHubToken is required.");
        }

        var correlationId = $"dev-auto-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31];
        var branchName = NormalizeBranchName(
            request.BranchName,
            mode,
            request.Title ?? request.Description,
            correlationId);

        var workflowRef = string.IsNullOrWhiteSpace(request.BaseBranch)
            ? options.DefaultBranch
            : request.BaseBranch.Trim();

        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? BuildDefaultPrompt(mode, request)
            : request.Prompt.Trim();

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = mode.ToLowerInvariant(),
            ["title"] = (request.Title ?? DefaultTitle(mode)).Trim(),
            ["description"] = request.Description.Trim(),
            ["prompt"] = prompt,
            ["branch_name"] = branchName,
            ["deploy_environment"] = string.IsNullOrWhiteSpace(request.DeployEnvironment)
                ? options.DefaultDeployEnvironment
                : request.DeployEnvironment.Trim(),
            ["auto_deploy"] = request.AutoDeploy ? "true" : "false",
            ["correlation_id"] = correlationId,
        };

        var payload = new
        {
            @ref = workflowRef,
            inputs,
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(options.RepositoryOwner)}/{Uri.EscapeDataString(options.RepositoryName)}/actions/workflows/{Uri.EscapeDataString(options.WorkflowFile)}/dispatches")
        {
            Content = JsonContent.Create(payload),
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GitHubToken);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Developer automation dispatch failed with {StatusCode}: {Body}",
                response.StatusCode,
                body);

            throw new InvalidOperationException(
                $"GitHub Actions dispatch failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var workflowUrl =
            $"https://github.com/{options.RepositoryOwner}/{options.RepositoryName}/actions/workflows/{options.WorkflowFile}";

        return new DeveloperAutomationResponseDto
        {
            Queued = true,
            Message = $"{DefaultTitle(mode)} automation queued.",
            WorkflowUrl = workflowUrl,
            BranchName = branchName,
            CorrelationId = correlationId,
        };
    }

    private static string BuildDefaultPrompt(string mode, DeveloperAutomationRequestDto request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(mode.Equals("bugfix", StringComparison.OrdinalIgnoreCase)
            ? "Fix the production bug described below."
            : "Implement the feature or enhancement described below.");
        builder.AppendLine();
        builder.AppendLine("Requirements:");
        builder.AppendLine("- Make the smallest safe code change that satisfies the request.");
        builder.AppendLine("- Preserve existing behavior unless the request explicitly changes it.");
        builder.AppendLine("- Build and test the repository before committing.");
        builder.AppendLine("- Do not include secrets in code, logs, commit messages, or pull-request text.");
        builder.AppendLine();
        builder.AppendLine("Request:");
        builder.AppendLine(request.Description.Trim());

        return builder.ToString();
    }

    private static string DefaultTitle(string mode)
        => mode.Equals("bugfix", StringComparison.OrdinalIgnoreCase)
            ? "AI Bug Fix"
            : "AI Feature / Enhancement";

    private static string NormalizeBranchName(
        string? requestedBranch,
        string mode,
        string title,
        string correlationId)
    {
        if (!string.IsNullOrWhiteSpace(requestedBranch))
        {
            return SanitizeBranchName(requestedBranch);
        }

        var slug = Slug(title);
        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        return SanitizeBranchName($"ai/{mode.ToLowerInvariant()}-{slug}-{correlationId[^8..]}");
    }

    private static string SanitizeBranchName(string value)
    {
        var sanitized = value.Trim().Replace('\\', '/');
        while (sanitized.Contains("//", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("//", "/", StringComparison.Ordinal);
        }

        sanitized = string.Join(
            '/',
            sanitized
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Slug)
                .Where(segment => !string.IsNullOrWhiteSpace(segment)));

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = $"ai/change-{Guid.NewGuid():N}"[..18];
        }

        return sanitized;
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(ch);
                previousDash = false;
                continue;
            }

            if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
