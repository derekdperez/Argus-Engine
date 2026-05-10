using ArgusEngine.CommandCenter.Web.Clients;
using ArgusEngine.CommandCenter.Web.Components;
using Microsoft.AspNetCore.Components;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();

// Register the Radzen services used by the Web UI without depending on the
// AddRadzenComponents extension method being visible to this project at compile time.
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped<ThemeService>();

builder.Services.AddScoped<LocalDockerClient>();

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = ResolveRequestBaseAddress(sp)
});

builder.Services.AddHttpClient<DiscoveryApiClient>((sp, client) =>
    client.BaseAddress = ResolveRequestBaseAddress(sp));

builder.Services.AddHttpClient<MaintenanceApiClient>((sp, client) =>
    client.BaseAddress = ResolveRequestBaseAddress(sp));

builder.Services.AddHttpClient<OperationsApiClient>((sp, client) =>
    client.BaseAddress = ResolveRequestBaseAddress(sp));

builder.Services.AddHttpClient<RealtimeApiClient>((sp, client) =>
    client.BaseAddress = ResolveRequestBaseAddress(sp));

builder.Services.AddHttpClient<UpdatesApiClient>((sp, client) =>
    client.BaseAddress = ResolveRequestBaseAddress(sp));

builder.Services.AddHttpClient<WorkerControlApiClient>((sp, client) =>
    client.BaseAddress = ResolveRequestBaseAddress(sp));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.MapStaticAssets();
app.UseStaticFiles();

// Compatibility alias for clients, proxies, or stale HTML that still request the
// pre-split Command Center CSS isolation bundle name. The current web project
// emits ArgusEngine.CommandCenter.Web.styles.css.
app.MapGet("/ArgusEngine.CommandCenter.styles.css", () =>
    Results.Redirect("/ArgusEngine.CommandCenter.Web.styles.css", permanent: false));

app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);

static Uri ResolveRequestBaseAddress(IServiceProvider services)
{
    var request = services.GetService<IHttpContextAccessor>()?.HttpContext?.Request;

    if (request is not null)
    {
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : "";
        return new Uri($"{request.Scheme}://{request.Host}{pathBase}/");
    }

    var navigation = services.GetService<NavigationManager>();
    if (navigation is not null)
    {
        return new Uri(navigation.BaseUri);
    }

    return new Uri("http://localhost/");
}
