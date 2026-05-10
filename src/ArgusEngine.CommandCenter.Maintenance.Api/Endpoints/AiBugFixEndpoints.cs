using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

/// <summary>
/// Endpoints for the AI bug-fix automation workflow.
/// Uses raw SQL against the ai_bug_fix_runs table (created by ArgusDbSchemaPatches)
/// to avoid introducing an EF entity just for this feature table.
/// </summary>
public static class AiBugFixEndpoints
{
    private const string ConfigCallbackToken  = "Argus:AiBugFix:CallbackToken";
    private const string ConfigGitHubToken    = "Argus:AiBugFix:GitHubToken";
    private const string ConfigGitHubOwner    = "Argus:AiBugFix:GitHubOwner";
    private const string ConfigGitHubRepo     = "Argus:AiBugFix:GitHubRepo";
    private const string ConfigCallbackBase   = "Argus:AiBugFix:CallbackBaseUrl";
    private const int    MaxErrorsPerRun      = 1000;
    private const int    MaxFiles             = 30;
    private const long   MaxFileSizeBytes     = 512_000;

    private static readonly IReadOnlyList<string> AllowedPathPrefixes = new[]
    {
        "src/", "tests/", "scripts/", ".github/workflows/", "deploy/",
        "Directory.Build.props", "Directory.Packages.props",
        "ArgusEngine.slnx", "global.json",
    };

    public static IEndpointRouteBuilder MapAiBugFixEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Public (internal-network only in prod) ──────────────────────────

        app.MapGet(
                "/api/diagnostics/ai-bug-fixes",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var rows = await QueryRunsAsync(db, limit: 20, ct).ConfigureAwait(false);
                    return Results.Ok(rows);
                })
            .WithName("AiBugFixList")
            .AllowAnonymous();

        app.MapGet(
                "/api/diagnostics/ai-bug-fixes/{runId:guid}",
                async (Guid runId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var row = await QueryRunAsync(db, runId, ct).ConfigureAwait(false);
                    return row is null ? Results.NotFound() : Results.Ok(row);
                })
            .WithName("AiBugFixGet")
            .AllowAnonymous();

        app.MapPost(
                "/api/diagnostics/ai-bug-fixes",
                async (
                    CreateAiBugFixRunRequest body,
                    HttpContext http,
                    IConfiguration config,
                    ArgusDbContext db,
                    ILogger<AiBugFixEndpointsMarker> logger,
                    CancellationToken ct) =>
                {
                    // Enforce single-active-run limit
                    var activeCount = await CountActiveRunsAsync(db, ct).ConfigureAwait(false);
                    if (activeCount > 0)
                        return Results.Conflict(new { error = "An AI bug-fix run is already active. Cancel or wait for it to complete before starting another." });

                    var runId = Guid.NewGuid();
                    var now   = DateTimeOffset.UtcNow;

                    // The prompt will be built server-side by the workflow fetching /prompt-bundle.
                    // We store a placeholder here; the full prompt text is set in the prompt-bundle endpoint.
                    // The error snapshot JSON is passed in by the caller (the web UI sends the errors it already has loaded).
                    var errorSnapshotJson = JsonSerializer.Serialize(body);
                    var promptPlaceholder = $"Run {runId} created at {now:O}. Prompt bundle available at /api/diagnostics/ai-bug-fixes/{runId}/prompt-bundle";
                    var promptSha         = ComputeSha256(promptPlaceholder);

                    var ip        = http.Connection.RemoteIpAddress?.ToString();
                    var sourceUrl = http.Request.Headers["Referer"].FirstOrDefault();

                    await InsertRunAsync(db, runId, now, ip, sourceUrl,
                        AiBugFixRunStatus.DispatchingWorkflow, "Run created; dispatching GitHub Actions workflow.",
                        0, Array.Empty<string>(), errorSnapshotJson, promptPlaceholder, promptSha, ct)
                        .ConfigureAwait(false);

                    // Dispatch the GitHub Actions workflow
                    string? workflowUrl = null;
                    try
                    {
                        var callbackBase  = config[ConfigCallbackBase] ?? DeriveCallbackBase(http);
                        var promptBundleUrl = $"{callbackBase.TrimEnd('/')}/api/diagnostics/ai-bug-fixes/{runId}/prompt-bundle";
                        workflowUrl = await DispatchGitHubWorkflowAsync(config, runId, promptBundleUrl, logger, ct).ConfigureAwait(false);

                        await UpdateRunWorkflowAsync(db, runId, workflowUrl, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to dispatch GitHub Actions AI bug-fix workflow for run {RunId}.", runId);
                        await FailRunAsync(db, runId, $"GitHub workflow dispatch failed: {ex.Message}", ct).ConfigureAwait(false);
                        return Results.Ok(new CreateAiBugFixRunResponse(
                            runId, AiBugFixRunStatus.Failed, null,
                            $"Run created but GitHub workflow dispatch failed: {ex.Message}"));
                    }

                    return Results.Ok(new CreateAiBugFixRunResponse(
                        runId, AiBugFixRunStatus.DispatchingWorkflow, workflowUrl,
                        "AI bug-fix run started. GitHub Actions workflow dispatched."));
                })
            .WithName("AiBugFixCreate")
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost(
                "/api/diagnostics/ai-bug-fixes/{runId:guid}/cancel",
                async (Guid runId, ArgusDbContext db, CancellationToken ct) =>
                {
                    var run = await QueryRunAsync(db, runId, ct).ConfigureAwait(false);
                    if (run is null) return Results.NotFound();
                    if (AiBugFixRunStatus.IsTerminal(run.Status))
                        return Results.BadRequest(new { error = $"Run is already in terminal state: {run.Status}." });

                    await SetRunStatusAsync(db, runId, AiBugFixRunStatus.Canceled, "Canceled by user.", ct).ConfigureAwait(false);
                    return Results.Ok(new { canceled = true, runId });
                })
            .WithName("AiBugFixCancel")
            .DisableAntiforgery()
            .AllowAnonymous();

        app.MapPost(
                "/api/diagnostics/ai-bug-fixes/{runId:guid}/retry",
                async (
                    Guid runId,
                    HttpContext http,
                    IConfiguration config,
                    ArgusDbContext db,
                    ILogger<AiBugFixEndpointsMarker> logger,
                    CancellationToken ct) =>
                {
                    var run = await QueryRunAsync(db, runId, ct).ConfigureAwait(false);
                    if (run is null) return Results.NotFound();
                    if (run.Status != AiBugFixRunStatus.Failed)
                        return Results.BadRequest(new { error = $"Only failed runs can be retried. Current status: {run.Status}." });

                    var activeCount = await CountActiveRunsAsync(db, ct).ConfigureAwait(false);
                    if (activeCount > 0)
                        return Results.Conflict(new { error = "Another AI bug-fix run is already active." });

                    await SetRunStatusAsync(db, runId, AiBugFixRunStatus.DispatchingWorkflow, "Retrying: dispatching workflow again.", ct).ConfigureAwait(false);

                    try
                    {
                        var callbackBase    = config[ConfigCallbackBase] ?? DeriveCallbackBase(http);
                        var promptBundleUrl = $"{callbackBase.TrimEnd('/')}/api/diagnostics/ai-bug-fixes/{runId}/prompt-bundle";
                        var workflowUrl     = await DispatchGitHubWorkflowAsync(config, runId, promptBundleUrl, logger, ct).ConfigureAwait(false);
                        await UpdateRunWorkflowAsync(db, runId, workflowUrl, ct).ConfigureAwait(false);
                        return Results.Ok(new { retried = true, runId, workflowUrl });
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Retry failed for run {RunId}.", runId);
                        await FailRunAsync(db, runId, $"Retry dispatch failed: {ex.Message}", ct).ConfigureAwait(false);
                        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
                    }
                })
            .WithName("AiBugFixRetry")
            .DisableAntiforgery()
            .AllowAnonymous();

        // ── Internal: called by GitHub Actions only ──────────────────────────

        app.MapGet(
                "/api/diagnostics/ai-bug-fixes/{runId:guid}/prompt-bundle",
                async (Guid runId, HttpContext http, IConfiguration config, ArgusDbContext db, CancellationToken ct) =>
                {
                    if (!RequireCallbackToken(http, config, out var denied))
                        return denied!;

                    var run = await QueryRunRawAsync(db, runId, ct).ConfigureAwait(false);
                    if (run is null) return Results.NotFound();

                    var callbackBase = config[ConfigCallbackBase] ?? DeriveCallbackBase(http);
                    var bundle = new AiBugFixPromptBundleDto(
                        runId,
                        run.prompt_text,
                        config[ConfigGitHubOwner] ?? "derekdperez",
                        config[ConfigGitHubRepo]  ?? "Argus-Engine",
                        callbackBase,
                        MaxFiles,
                        MaxFileSizeBytes,
                        AllowedPathPrefixes);

                    return Results.Ok(bundle);
                })
            .WithName("AiBugFixPromptBundle")
            .AllowAnonymous();

        app.MapPost(
                "/api/internal/diagnostics/ai-bug-fixes/{runId:guid}/workflow-callback",
                async (
                    Guid runId,
                    AiBugFixWorkflowCallbackRequest body,
                    HttpContext http,
                    IConfiguration config,
                    ArgusDbContext db,
                    ILogger<AiBugFixEndpointsMarker> logger,
                    CancellationToken ct) =>
                {
                    if (!RequireCallbackToken(http, config, out var denied))
                        return denied!;

                    var run = await QueryRunAsync(db, runId, ct).ConfigureAwait(false);
                    if (run is null) return Results.NotFound();

                    logger.LogInformation(
                        "AI bug-fix workflow callback: runId={RunId} status={Status} pr={Pr}",
                        runId, body.Status, body.PullRequestNumber);

                    await ApplyCallbackAsync(db, runId, body, ct).ConfigureAwait(false);
                    return Results.Ok(new { accepted = true });
                })
            .WithName("AiBugFixWorkflowCallback")
            .DisableAntiforgery()
            .AllowAnonymous();

        return app;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool RequireCallbackToken(HttpContext http, IConfiguration config, out IResult? denied)
    {
        var required = config[ConfigCallbackToken]?.Trim();
        if (string.IsNullOrWhiteSpace(required))
        {
            denied = Results.Problem(
                title: "AI bug-fix callback endpoint misconfigured",
                detail: $"{ConfigCallbackToken} must be set.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
            return false;
        }

        var header = http.Request.Headers["Authorization"].FirstOrDefault() ?? string.Empty;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            header = header["Bearer ".Length..].Trim();

        if (!string.Equals(header, required, StringComparison.Ordinal))
        {
            denied = Results.Unauthorized();
            return false;
        }

        denied = null;
        return true;
    }

    private static async Task<string?> DispatchGitHubWorkflowAsync(
        IConfiguration config, Guid runId, string promptBundleUrl,
        ILogger logger, CancellationToken ct)
    {
        var token = config[ConfigGitHubToken]?.Trim();
        var owner = config[ConfigGitHubOwner]?.Trim() ?? "derekdperez";
        var repo  = config[ConfigGitHubRepo]?.Trim()  ?? "Argus-Engine";

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("GitHub token not configured ({Key}); skipping workflow dispatch.", ConfigGitHubToken);
            return null;
        }

        using var http   = new HttpClient();
        http.DefaultRequestHeaders.Add("Accept",               "application/vnd.github+json");
        http.DefaultRequestHeaders.Add("Authorization",        $"Bearer {token}");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Add("User-Agent",           "ArgusEngine-MaintenanceApi/1.0");

        var payload = JsonSerializer.Serialize(new
        {
            @ref    = "main",
            inputs  = new
            {
                run_id            = runId.ToString(),
                prompt_bundle_url = promptBundleUrl,
            }
        });

        var url      = $"https://api.github.com/repos/{owner}/{repo}/actions/workflows/ai-bugfix.yml/dispatches";
        var content  = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"GitHub API returned {(int)response.StatusCode}: {body}");
        }

        return $"https://github.com/{owner}/{repo}/actions";
    }

    private static string DeriveCallbackBase(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}";

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Raw SQL ───────────────────────────────────────────────────────────────

    private static async Task InsertRunAsync(
        ArgusDbContext db, Guid id, DateTimeOffset now,
        string? ip, string? sourceUrl,
        string status, string? statusMessage,
        int errorCount, string[] componentScope,
        string errorSnapshotJson, string promptText, string promptSha,
        CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ai_bug_fix_runs (
                id, created_at_utc, updated_at_utc,
                requested_from_ip, source_url,
                status, status_message,
                error_count, component_scope,
                error_snapshot_json, prompt_text, prompt_sha256
            ) VALUES (
                {0}, {1}, {1},
                {2}, {3},
                {4}, {5},
                {6}, {7},
                {8}::jsonb, {9}, {10}
            )
            """,
            id, now, (object?)ip ?? DBNull.Value, (object?)sourceUrl ?? DBNull.Value,
            status, (object?)statusMessage ?? DBNull.Value,
            errorCount, componentScope,
            errorSnapshotJson, promptText, promptSha)
            .ConfigureAwait(false);
    }

    private static async Task SetRunStatusAsync(
        ArgusDbContext db, Guid id, string status, string? message, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE ai_bug_fix_runs SET status={0}, status_message={1}, updated_at_utc={2} WHERE id={3}",
            status, (object?)message ?? DBNull.Value, DateTimeOffset.UtcNow, id)
            .ConfigureAwait(false);
    }

    private static async Task FailRunAsync(ArgusDbContext db, Guid id, string detail, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE ai_bug_fix_runs SET status={0}, status_message={1}, failure_detail={2}, updated_at_utc={3} WHERE id={4}",
            AiBugFixRunStatus.Failed, "Run failed.", detail, DateTimeOffset.UtcNow, id)
            .ConfigureAwait(false);
    }

    private static async Task UpdateRunWorkflowAsync(ArgusDbContext db, Guid id, string? workflowUrl, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE ai_bug_fix_runs SET github_workflow_url={0}, status={1}, status_message={2}, updated_at_utc={3} WHERE id={4}",
            (object?)workflowUrl ?? DBNull.Value, AiBugFixRunStatus.AiGeneratingPatch, "GitHub workflow dispatched; AI is generating a patch.", DateTimeOffset.UtcNow, id)
            .ConfigureAwait(false);
    }

    private static async Task ApplyCallbackAsync(
        ArgusDbContext db, Guid id, AiBugFixWorkflowCallbackRequest cb, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE ai_bug_fix_runs SET
                status                      = {0},
                status_message              = {1},
                github_branch               = COALESCE({2}, github_branch),
                github_pr_number            = COALESCE({3}, github_pr_number),
                github_pr_url               = COALESCE({4}, github_pr_url),
                github_workflow_run_id      = COALESCE({5}, github_workflow_run_id),
                github_workflow_url         = COALESCE({6}, github_workflow_url),
                github_merge_sha            = COALESCE({7}, github_merge_sha),
                deployment_run_id           = COALESCE({8}, deployment_run_id),
                deployment_url              = COALESCE({9}, deployment_url),
                deployment_completed_at_utc = CASE WHEN {10}::text IS NOT NULL AND {10}::text <> '' THEN now() ELSE deployment_completed_at_utc END,
                smoke_test_result_json      = COALESCE({10}::jsonb, smoke_test_result_json),
                failure_detail              = COALESCE({11}, failure_detail),
                updated_at_utc              = {12}
            WHERE id = {13}
            """,
            cb.Status, (object?)cb.StatusMessage ?? DBNull.Value,
            (object?)cb.Branch ?? DBNull.Value, (object?)cb.PullRequestNumber ?? DBNull.Value, (object?)cb.PullRequestUrl ?? DBNull.Value,
            (object?)cb.WorkflowRunId ?? DBNull.Value, (object?)cb.WorkflowUrl ?? DBNull.Value,
            (object?)cb.MergeSha ?? DBNull.Value,
            (object?)cb.DeploymentRunId ?? DBNull.Value, (object?)cb.DeploymentUrl ?? DBNull.Value,
            (object?)cb.SmokeTestResultJson ?? DBNull.Value,
            (object?)cb.FailureDetail ?? DBNull.Value,
            DateTimeOffset.UtcNow,
            id)
            .ConfigureAwait(false);
    }

    private static async Task<int> CountActiveRunsAsync(ArgusDbContext db, CancellationToken ct)
    {
        var result = await db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM ai_bug_fix_runs WHERE status NOT IN ('Deployed','Failed','Canceled')")
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return result.FirstOrDefault();
    }

    private static async Task<List<AiBugFixRunDto>> QueryRunsAsync(ArgusDbContext db, int limit, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<AiBugFixRunRow>(
                """
                SELECT id, created_at_utc, updated_at_utc, status, status_message,
                       error_count, component_scope, github_branch, github_pr_number,
                       github_pr_url, github_workflow_run_id, github_workflow_url,
                       deployment_url, deployment_completed_at_utc, failure_detail
                FROM ai_bug_fix_runs
                ORDER BY created_at_utc DESC
                LIMIT {0}
                """, limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    private static async Task<AiBugFixRunDto?> QueryRunAsync(ArgusDbContext db, Guid id, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<AiBugFixRunRow>(
                """
                SELECT id, created_at_utc, updated_at_utc, status, status_message,
                       error_count, component_scope, github_branch, github_pr_number,
                       github_pr_url, github_workflow_run_id, github_workflow_url,
                       deployment_url, deployment_completed_at_utc, failure_detail
                FROM ai_bug_fix_runs WHERE id = {0}
                """, id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToDto).FirstOrDefault();
    }

    private static async Task<AiBugFixRunRaw?> QueryRunRawAsync(ArgusDbContext db, Guid id, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQueryRaw<AiBugFixRunRaw>(
                "SELECT id, prompt_text FROM ai_bug_fix_runs WHERE id = {0}", id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    private static AiBugFixRunDto ToDto(AiBugFixRunRow r) => new(
        r.id, r.created_at_utc, r.updated_at_utc,
        r.status, r.status_message,
        r.error_count, r.component_scope ?? [],
        r.github_branch, r.github_pr_number, r.github_pr_url,
        r.github_workflow_run_id, r.github_workflow_url,
        r.deployment_url, r.deployment_completed_at_utc, r.failure_detail);

    // Lightweight projection types for SqlQueryRaw
    private sealed class AiBugFixRunRow
    {
        public Guid             id                          { get; set; }
        public DateTimeOffset   created_at_utc              { get; set; }
        public DateTimeOffset   updated_at_utc              { get; set; }
        public string           status                      { get; set; } = "";
        public string?          status_message              { get; set; }
        public int              error_count                 { get; set; }
        public string[]?        component_scope             { get; set; }
        public string?          github_branch               { get; set; }
        public int?             github_pr_number            { get; set; }
        public string?          github_pr_url               { get; set; }
        public long?            github_workflow_run_id      { get; set; }
        public string?          github_workflow_url         { get; set; }
        public string?          deployment_url              { get; set; }
        public DateTimeOffset?  deployment_completed_at_utc { get; set; }
        public string?          failure_detail              { get; set; }
    }

    private sealed class AiBugFixRunRaw
    {
        public Guid   id          { get; set; }
        public string prompt_text { get; set; } = "";
    }
}

// Marker type used only as a generic parameter for ILogger DI resolution
internal sealed class AiBugFixEndpointsMarker;
