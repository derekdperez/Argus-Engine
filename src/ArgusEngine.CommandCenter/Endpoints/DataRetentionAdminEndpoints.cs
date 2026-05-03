using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ArgusEngine.Application.DataRetention;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.DataRetention;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class DataRetentionAdminEndpoints
{
    public static IEndpointRouteBuilder MapDataRetentionAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/data-retention/status", GetStatusAsync);
        app.MapPost("/api/admin/data-retention/run-now", RunNowAsync);
        app.MapPost("/api/admin/partitions/ensure", EnsurePartitionsAsync);

        return app;
    }

    private static IResult GetStatusAsync(
        IConfiguration configuration,
        DataRetentionRunState state,
        IOptions<DataRetentionOptions> options,
        HttpRequest request)
    {
        if (!IsAuthorized(configuration, request))
            return Results.Unauthorized();

        return Results.Ok(new
        {
            enabled = options.Value.Enabled,
            state.LastRunAtUtc,
            state.LastResult
        });
    }

    private static async Task<IResult> RunNowAsync(
        IConfiguration configuration,
        DataRetentionWorker worker,
        DataRetentionRunState state,
        IOptions<DataRetentionOptions> options,
        HttpRequest request,
        [FromBody] DataRetentionRunRequest? body,
        CancellationToken ct)
    {
        if (!IsAuthorized(configuration, request))
            return Results.Unauthorized();

        if (!string.Equals(body?.Confirmation, "RUN DATA RETENTION", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Confirmation phrase RUN DATA RETENTION is required." });

        var result = await worker.RunOnceAsync(options.Value, ct).ConfigureAwait(false);
        state.Record(result);

        return Results.Ok(result);
    }

    private static async Task<IResult> EnsurePartitionsAsync(
        IConfiguration configuration,
        IPartitionMaintenanceService partitions,
        HttpRequest request,
        CancellationToken ct)
    {
        if (!IsAuthorized(configuration, request))
            return Results.Unauthorized();

        await partitions.EnsurePartitionsAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { ensuredAtUtc = DateTimeOffset.UtcNow });
    }

    private static bool IsAuthorized(IConfiguration configuration, HttpRequest request)
    {
        var configuredKey = configuration.GetArgusValue("DataMaintenance:ApiKey");
        if (string.IsNullOrWhiteSpace(configuredKey))
            return true;

        var provided = request.Headers["X-Maintenance-Key"].FirstOrDefault()
                       ?? request.Headers["X-Argus-Maintenance-Key"].FirstOrDefault();

        return string.Equals(provided, configuredKey, StringComparison.Ordinal);
    }

    public sealed record DataRetentionRunRequest(string? Confirmation);
}
