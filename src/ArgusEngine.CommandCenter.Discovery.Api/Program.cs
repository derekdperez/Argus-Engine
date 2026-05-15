using ArgusEngine.Application.Orchestration;
using ArgusEngine.CommandCenter.Discovery.Api.Endpoints;
using ArgusEngine.CommandCenter.Discovery.Api.Services;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });
builder.Services.AddScoped<RootSpiderSeedService>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet(
    "/health/ready",
    async (ArgusDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct).ConfigureAwait(false)
            ? Results.Ok(new { status = "ready", postgres = "ok" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.MapAssetAdmissionDecisionEndpoints();
app.MapAssetEndpoints();
app.MapAssetGraphEndpoints();
app.MapEventTraceEndpoints();
app.MapFileStoreEndpoints();
app.MapHighValueFindingEndpoints();
app.MapHttpRequestQueueEndpoints();
app.MapTagEndpoints();
app.MapTargetEndpoints();
app.MapTechnologyIdentificationEndpoints();
MapReconAgentEndpointsLocal(app);

await app.RunAsync().ConfigureAwait(false);

static void MapReconAgentEndpointsLocal(WebApplication app)
{
    var group = app.MapGroup("/api/recon-agent");

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
}

sealed record AttachReconAgentRequest(
    string? AttachedBy,
    ReconOrchestratorConfiguration? Configuration);

sealed record AttachReconAgentResponse(
    ReconOrchestratorSnapshot Snapshot,
    ReconOrchestratorTickResult InitialTick);
