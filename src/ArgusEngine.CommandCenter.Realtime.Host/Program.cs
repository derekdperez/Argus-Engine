using ArgusEngine.CommandCenter.Realtime.Host.Hubs;
using ArgusEngine.CommandCenter.Realtime.Host.Services;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, x => x.AddConsumer<LiveUiEventConsumer>());
builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRRealtimeUpdatePublisher>();
builder.Services.AddSingleton<IRealtimeUpdatePublisher>(sp => sp.GetRequiredService<SignalRRealtimeUpdatePublisher>());

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();

app.MapGet(
        "/health/ready",
        async (ArgusDbContext db, CancellationToken ct) =>
        {
            var canConnect = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);

            return canConnect
                ? Results.Ok(new { status = "ready", postgres = "ok", signalr = "ok" })
                : Results.Json(
                    new { status = "unhealthy", postgres = "unreachable", signalr = "unknown" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        })
    .AllowAnonymous();

app.MapHub<DiscoveryHub>("/hubs/discovery");

await app.RunAsync().ConfigureAwait(false);
