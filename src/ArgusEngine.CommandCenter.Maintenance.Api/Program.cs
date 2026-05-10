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



app.MapAdminUsageEndpoints();
app.MapAiBugFixEndpoints();
app.MapBusJournalEndpoints();
app.MapDataMaintenanceEndpoints();
app.MapDataRetentionAdminEndpoints();
app.MapDiagnosticsEndpoints();
app.MapHttpArtifactBackfillEndpoints();


await app.RunAsync().ConfigureAwait(false);
