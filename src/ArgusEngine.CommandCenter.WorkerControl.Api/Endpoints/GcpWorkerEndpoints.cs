using ArgusEngine.CommandCenter.WorkerControl.Api.Services;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Endpoints;

public static class GcpWorkerEndpoints
{
    private static readonly string[] DefaultSlugs = ["spider", "http-requester", "enum", "portscan", "highvalue", "techid"];

    public static IEndpointRouteBuilder MapGcpWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/gcp-workers");

        group.MapGet("/status", async (GcpCloudRunClient gcp, CancellationToken ct) =>
        {
            if (!gcp.IsConfigured)
                return Results.Ok(new { configured = false, workers = Array.Empty<object>() });

            var workers = await gcp.GetWorkerStatusesAsync(ct);
            return Results.Ok(new { configured = true, workers });
        });

        group.MapPost("/deploy", async (DeployGcpWorkerRequest body, GcpCloudRunClient gcp, CancellationToken ct) =>
        {
            var result = await gcp.DeployWorkerAsync(body.Slug, body.MinInstances, body.MaxInstances, ct);
            return result is null ? Results.Problem("Failed to deploy worker") : Results.Ok(result);
        });

        group.MapPost("/deploy-all", async (GcpCloudRunClient gcp, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("GcpWorkerEndpoints");
            var results = new List<object>();
            foreach (var slug in DefaultSlugs)
            {
                logger.LogInformation("Deploying worker {Slug} with min=1, max=2", slug);
                var result = await gcp.DeployWorkerAsync(slug, 1, 2, ct);
                results.Add(new { slug, success = result is not null, service = result?.Name, url = result?.Url });
            }
            return Results.Ok(new { results });
        });

        group.MapPut("/scale", async (ScaleGcpWorkersRequest body, GcpCloudRunClient gcp, CancellationToken ct) =>
        {
            var results = new List<object>();
            var slugs = body.Workers ?? DefaultSlugs;
            foreach (var slug in slugs)
            {
                var ok = await gcp.ScaleWorkerAsync(slug, body.MinInstances, body.MaxInstances, ct);
                results.Add(new { slug, success = ok });
            }
            return Results.Ok(new { results });
        });

        return app;
    }
}

public sealed record DeployGcpWorkerRequest(string Slug, int MinInstances = 1, int MaxInstances = 2);
public sealed record ScaleGcpWorkersRequest(int MinInstances, int MaxInstances, string[]? Workers = null);
