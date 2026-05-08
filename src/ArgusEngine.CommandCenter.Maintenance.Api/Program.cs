using ArgusEngine.CommandCenter.Maintenance.Api;
using ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });
builder.Services.AddScoped<HttpQueueArtifactBackfillService>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

app.MapGet(
    "/health/ready",
    async (ArgusDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct).ConfigureAwait(false)
            ? Results.Ok(new { status = "ready", postgres = "ok" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.MapAdminUsageEndpoints();
app.MapBusJournalEndpoints();
app.MapDataMaintenanceEndpoints();
app.MapDataRetentionAdminEndpoints();
app.MapDiagnosticsEndpoints();
app.MapHttpArtifactBackfillEndpoints();

await app.RunAsync().ConfigureAwait(false);
