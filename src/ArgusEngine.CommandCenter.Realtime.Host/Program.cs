using ArgusEngine.CommandCenter.Realtime.Host.Hubs;
using ArgusEngine.CommandCenter.Realtime.Host.Services;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddArgusInfrastructure(builder.Configuration, enableOutboxDispatcher: false);
builder.Services.AddArgusRabbitMq(builder.Configuration, x => x.AddConsumer<LiveUiEventConsumer>());
builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRRealtimeUpdatePublisher>();
builder.Services.AddSingleton<IRealtimeUpdatePublisher>(sp => sp.GetRequiredService<SignalRRealtimeUpdatePublisher>());

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
app.MapHub<DiscoveryHub>("/hubs/discovery");

await app.RunAsync().ConfigureAwait(false);
