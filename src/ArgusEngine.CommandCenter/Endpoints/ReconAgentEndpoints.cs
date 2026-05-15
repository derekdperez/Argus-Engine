using ArgusEngine.Application.Orchestration;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class ReconAgentEndpoints
{
    public static IEndpointRouteBuilder MapReconAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recon-agent");

        group.MapGet("/targets/{targetId:guid}", async (
            Guid targetId,
            IReconOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await orchestrator.GetSnapshotAsync(targetId, cancellationToken).ConfigureAwait(false);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        group.MapPost("/targets/{targetId:guid}/attach", async (
            Guid targetId,
            AttachReconAgentRequest request,
            IReconOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await orchestrator.AttachToTargetAsync(
                    targetId,
                    string.IsNullOrWhiteSpace(request.AttachedBy) ? "command-center" : request.AttachedBy,
                    request.Configuration,
                    cancellationToken)
                .ConfigureAwait(false);

            var tick = await orchestrator.TickTargetAsync(
                    targetId,
                    $"command-center-{Environment.MachineName}",
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new AttachReconAgentResponse(snapshot, tick));
        });

        return app;
    }
}

public sealed record AttachReconAgentRequest(
    string? AttachedBy,
    ReconOrchestratorConfiguration? Configuration);

public sealed record AttachReconAgentResponse(
    ReconOrchestratorSnapshot Snapshot,
    ReconOrchestratorTickResult InitialTick);
