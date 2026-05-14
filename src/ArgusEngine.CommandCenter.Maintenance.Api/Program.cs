using ArgusEngine.CommandCenter.Maintenance.Api;
using ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, _ => { });
builder.Services.AddScoped<HttpQueueArtifactBackfillService>();

var app = builder.Build();

await ArgusDbBootstrap.InitializeAsync(
    app.Services,
    app.Configuration,
    app.Logger,
    includeFileStore: false).ConfigureAwait(false);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();

// /health/ready is registered by MapDiagnosticsEndpoints below. Do not map it
// here as well, or ASP.NET Core treats health checks as ambiguous matches.
app.MapAdminUsageEndpoints();
app.MapBusJournalEndpoints();
app.MapDataMaintenanceEndpoints();
app.MapDataRetentionAdminEndpoints();
app.MapDiagnosticsEndpoints();
app.MapHttpArtifactBackfillEndpoints();
app.MapUiPreferencesEndpoints();

await app.RunAsync().ConfigureAwait(false);
