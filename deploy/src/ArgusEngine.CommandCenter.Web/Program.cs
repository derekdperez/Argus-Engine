using ArgusEngine.CommandCenter.Web.Clients;
using ArgusEngine.CommandCenter.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();

// Register the Radzen services used by the Web UI without depending on the
// AddRadzenComponents extension method being visible to this project at compile time.
//
// RadzenTheme injects ThemeService during prerender. Without this registration the
// app returns HTTP 500 on the root page even though /health/ready is healthy.
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped<ThemeService>();

builder.Services.AddScoped<LocalDockerClient>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = ResolveRequestBaseAddress(sp) });

builder.Services.AddHttpClient<DiscoveryApiClient>((sp, client) =>
    client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<OperationsApiClient>((sp, client) =>
    client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<WorkerControlApiClient>((sp, client) =>
    client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<MaintenanceApiClient>((sp, client) =>
    client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<UpdatesApiClient>((sp, client) =>
    client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<RealtimeApiClient>((sp, client) =>
    client.BaseAddress = ResolveGatewayBaseAddress(sp));

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

app
    .MapRazorComponents<App>()
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

static Uri ResolveGatewayBaseAddress(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>();

    var configured =
        configuration["CommandCenter:GatewayBaseUrl"]
        ?? configuration["Argus:CommandCenter:GatewayBaseUrl"]
        ?? configuration["CommandCenter:Services:Gateway"]
        ?? configuration["Argus:CommandCenter:Services:Gateway"];

    if (!string.IsNullOrWhiteSpace(configured))
    {
        if (!Uri.TryCreate(EnsureTrailingSlash(configured), UriKind.Absolute, out var configuredUri))
        {
            throw new InvalidOperationException(
                $"Invalid Command Center gateway URL '{configured}'. Configure CommandCenter:GatewayBaseUrl.");
        }

        return configuredUri;
    }

    return ResolveRequestBaseAddress(services);
}

static string EnsureTrailingSlash(string value) =>
    value.Length > 0 && value[^1] == '/' ? value : value + "/";
