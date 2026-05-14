using ArgusEngine.CloudDeploy;
using Microsoft.AspNetCore.Mvc;

namespace ArgusEngine.CommandCenter.CloudDeploy.Api;

public static class CloudDeployEndpoints
{
    public static IEndpointRouteBuilder MapCloudDeployEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cloud-deploy")
            .WithTags("CloudDeploy");

        // ── Preflight ─────────────────────────────────────────────────────────
        group.MapGet("/preflight", async (
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            var issues = await svc.RunPreflightAsync(ct);
            return issues.Count == 0
                ? Results.Ok(new PreflightResponse(true, []))
                : Results.Ok(new PreflightResponse(false, issues));
        });

        // ── Status ────────────────────────────────────────────────────────────
        group.MapGet("/workers/status", async (
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            var statuses = await svc.GetWorkerStatusesAsync(null, ct);
            return Results.Ok(statuses);
        });

        group.MapGet("/workers/{worker}/status", async (
            string worker,
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            if (!TryParseWorker(worker, out var workerType))
                return Results.BadRequest($"Unknown worker type: {worker}");

            var statuses = await svc.GetWorkerStatusesAsync([workerType], ct);
            return Results.Ok(statuses.Count > 0 ? statuses[0] : null);
        });

        // ── Build & Push ──────────────────────────────────────────────────────
        // Returns a stream of progress events (SSE)
        group.MapPost("/workers/build", async (
            [FromBody] WorkerListRequest? req,
            IGcpHybridDeployService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var workers = ParseWorkerList(req?.Workers);
            var progress = new SseProgress(ctx.Response);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            var result = await svc.BuildAndPushImagesAsync(workers, progress, ct);
            await ctx.Response.WriteAsync(
                $"data: {System.Text.Json.JsonSerializer.Serialize(result)}\n\n", ct);
        });

        // ── Deploy ────────────────────────────────────────────────────────────
        group.MapPost("/workers/deploy", async (
            [FromBody] WorkerListRequest? req,
            IGcpHybridDeployService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var workers = ParseWorkerList(req?.Workers);
            var progress = new SseProgress(ctx.Response);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            var result = await svc.DeployWorkersAsync(workers, progress, ct);
            await ctx.Response.WriteAsync(
                $"data: {System.Text.Json.JsonSerializer.Serialize(result)}\n\n", ct);
        });

        group.MapPost("/workers/{worker}/deploy", async (
            string worker,
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            if (!TryParseWorker(worker, out var workerType))
                return Results.BadRequest($"Unknown worker type: {worker}");

            var result = await svc.DeployWorkerAsync(workerType, null, ct);
            return result.Success
                ? Results.Ok(result)
                : Results.Problem(result.Message);
        });

        // ── Scale ─────────────────────────────────────────────────────────────
        group.MapPut("/workers/scale", async (
            [FromBody] ScaleRequest req,
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            var workers = ParseWorkerList(req.Workers);
            var result = await svc.ScaleWorkersAsync(req.MinInstances, req.MaxInstances, workers, ct);
            return result.AllSucceeded
                ? Results.Ok(result)
                : Results.Problem(string.Join("; ", result.Failures.Select(f => f.Result.Message)));
        });

        // ── Teardown ──────────────────────────────────────────────────────────
        group.MapDelete("/workers", async (
            [FromBody] WorkerListRequest? req,
            IGcpHybridDeployService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var workers = ParseWorkerList(req?.Workers);
            var progress = new SseProgress(ctx.Response);

            ctx.Response.Headers.ContentType = "text/event-stream";

            var result = await svc.TeardownWorkersAsync(workers, progress, ct);
            await ctx.Response.WriteAsync(
                $"data: {System.Text.Json.JsonSerializer.Serialize(result)}\n\n", ct);
        });

        // ── Local core ────────────────────────────────────────────────────────
        group.MapPost("/core/start", async (
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            var result = await svc.StartLocalCoreAsync(null, ct);
            return result.Success ? Results.Ok(result) : Results.Problem(result.Message);
        });

        group.MapPost("/core/stop", async (
            IGcpHybridDeployService svc,
            CancellationToken ct) =>
        {
            var result = await svc.StopLocalCoreAsync(null, ct);
            return result.Success ? Results.Ok(result) : Results.Problem(result.Message);
        });

        return app;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseWorker(string slug, out WorkerType result)
    {
        foreach (var w in WorkerTypeExtensions.All())
        {
            if (w.ToSlug().Equals(slug, StringComparison.OrdinalIgnoreCase))
            {
                result = w;
                return true;
            }
        }
        result = default;
        return false;
    }

    private static IEnumerable<WorkerType>? ParseWorkerList(IEnumerable<string>? slugs)
    {
        if (slugs is null) return null;

        return slugs
            .Select(s => WorkerTypeExtensions.All().FirstOrDefault(w =>
                w.ToSlug().Equals(s, StringComparison.OrdinalIgnoreCase)))
            .Where(w => w != default);
    }

    // ── SSE progress writer ───────────────────────────────────────────────────

    private sealed class SseProgress(HttpResponse response) : IProgress<DeployProgressEvent>
    {
        public void Report(DeployProgressEvent value)
        {
            // Fire-and-forget write — acceptable for SSE progress events
            _ = response.WriteAsync(
                $"data: {System.Text.Json.JsonSerializer.Serialize(value)}\n\n");
            _ = response.Body.FlushAsync();
        }
    }
}

// ── Request / response DTOs ────────────────────────────────────────────────────

public record WorkerListRequest(IEnumerable<string>? Workers);

public record ScaleRequest(
    int                      MinInstances,
    int                      MaxInstances,
    IEnumerable<string>?     Workers = null);

public record PreflightResponse(
    bool                     Ready,
    IReadOnlyList<string>    Issues);
