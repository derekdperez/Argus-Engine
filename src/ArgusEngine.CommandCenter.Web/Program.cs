using ArgusEngine.CommandCenter.Realtime;
using ArgusEngine.CommandCenter.Web.Clients;
using ArgusEngine.CommandCenter.Web.Components;
using Microsoft.AspNetCore.Components;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();

// Radzen components used by App.razor/layout/pages.
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped<ThemeService>();

// Plain HttpClient remains request-relative for components that deliberately use the web host.
// Split-service API clients below must not use the current request host, because in Docker that
// resolves to command-center-web:8080 and causes /api/* calls to hit the Blazor app instead of
// the gateway/API services.
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = ResolveRequestBaseAddress(sp),
});

builder.Services.AddHttpClient<DiscoveryApiClient>((sp, client) =>
    client.BaseAddress = ResolveServiceBaseAddress(
        sp,
        "Discovery",
        "http://command-center-discovery-api:8080/"));

builder.Services.AddHttpClient<OperationsApiClient>((sp, client) =>
    client.BaseAddress = ResolveServiceBaseAddress(
        sp,
        "Operations",
        "http://command-center-operations-api:8080/"));

builder.Services.AddHttpClient<WorkerControlApiClient>((sp, client) =>
    client.BaseAddress = ResolveServiceBaseAddress(
        sp,
        "WorkerControl",
        "http://command-center-worker-control-api:8080/"));

builder.Services.AddHttpClient<MaintenanceApiClient>((sp, client) =>
    client.BaseAddress = ResolveServiceBaseAddress(
        sp,
        "Maintenance",
        "http://command-center-maintenance-api:8080/"));

builder.Services.AddHttpClient<UpdatesApiClient>((sp, client) =>
    client.BaseAddress = ResolveServiceBaseAddress(
        sp,
        "Updates",
        "http://command-center-updates-api:8080/"));

builder.Services.AddHttpClient<RealtimeApiClient>((sp, client) =>
    client.BaseAddress = ResolveServiceBaseAddress(
        sp,
        "Realtime",
        "http://command-center-realtime:8080/"));

builder.Services.AddScoped<DiscoveryRealtimeClient>();
builder.Services.AddScoped<LocalDockerClient>();

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
app.MapGet(
    "/ArgusEngine.CommandCenter.styles.css",
    () => Results.Redirect("/ArgusEngine.CommandCenter.Web.styles.css", permanent: false));

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

static Uri ResolveServiceBaseAddress(IServiceProvider services, string serviceName, string localDefault)
{
    var configuration = services.GetRequiredService<IConfiguration>();

    var value =
        configuration[$"CommandCenter:Services:{serviceName}"] ??
        configuration[$"Argus:CommandCenter:Services:{serviceName}"] ??
        configuration[$"CommandCenter:{serviceName}BaseUrl"] ??
        configuration[$"Argus:CommandCenter:{serviceName}BaseUrl"] ??
        localDefault;

    if (Uri.TryCreate(EnsureTrailingSlash(value), UriKind.Absolute, out var uri))
    {
        return uri;
    }

    throw new InvalidOperationException(
        $"Invalid Command Center split-service URL for '{serviceName}': '{value}'. " +
        $"Configure CommandCenter:Services:{serviceName}.");
}

static string EnsureTrailingSlash(string value) =>
    value.Length > 0 && value[^1] == '/' ? value : value + "/";
