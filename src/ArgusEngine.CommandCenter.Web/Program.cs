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

// Register the Radzen services used by the Web UI without relying on the
// AddRadzenComponents extension method being visible to this project at compile time.
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped<ThemeService>();

builder.Services.AddScoped<LocalDockerClient>();
builder.Services.AddScoped<DiscoveryRealtimeClient>();

// Blazor server components execute on the server.  Relative HttpClient calls
// must therefore target the Command Center gateway, not command-center-web
// itself.  The gateway owns split-service routing for /api and /hubs paths.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = ResolveGatewayBaseAddress(sp) });

builder.Services.AddHttpClient<DiscoveryApiClient>((sp, client) => client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<OperationsApiClient>((sp, client) => client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<WorkerControlApiClient>((sp, client) => client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<MaintenanceApiClient>((sp, client) => client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<UpdatesApiClient>((sp, client) => client.BaseAddress = ResolveGatewayBaseAddress(sp));
builder.Services.AddHttpClient<RealtimeApiClient>((sp, client) => client.BaseAddress = ResolveGatewayBaseAddress(sp));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Prevent browsers from caching the HTML shell — it embeds fingerprinted asset URLs
// (blazor.web.js, app.css, etc.) that change on every deploy. A stale cached shell
// causes 404s for those assets until the user hard-refreshes.


app.MapStaticAssets();
app.UseStaticFiles();

app.MapGet("/_framework/blazor.web.js", async context =>
{
    var path = Path.Combine(builder.Environment.WebRootPath ?? "wwwroot", "_framework", "blazor.web.js");
    if (File.Exists(path))
    {
        context.Response.ContentType = "application/javascript";
        await context.Response.SendFileAsync(path);
        return;
    }

    // Fallback 1: Deep search in /usr/share/dotnet
    var sharedFxDir = "/usr/share/dotnet";
    if (Directory.Exists(sharedFxDir))
    {
        try
        {
            var files = Directory.GetFiles(sharedFxDir, "blazor.web.js", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                context.Response.ContentType = "application/javascript";
                await context.Response.SendFileAsync(files[0]);
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error searching /usr/share/dotnet: {ex.Message}");
        }
    }

    // Fallback 2: Deep search in /app
    var appDir = "/app";
    if (Directory.Exists(appDir))
    {
        try
        {
            var files = Directory.GetFiles(appDir, "blazor.web.js", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                context.Response.ContentType = "application/javascript";
                await context.Response.SendFileAsync(files[0]);
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error searching /app: {ex.Message}");
        }
    }

    context.Response.StatusCode = 404;
    await context.Response.WriteAsync($"blazor.web.js not found on disk at {path} or in shared framework (deep search failed). WebRootPath is {builder.Environment.WebRootPath}");
});

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

static Uri ResolveGatewayBaseAddress(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>();

    var configured =
        configuration["CommandCenter:GatewayBaseUrl"] ??
        configuration["Argus:CommandCenter:GatewayBaseUrl"] ??
        configuration["CommandCenter:Services:Gateway"] ??
        configuration["Argus:CommandCenter:Services:Gateway"];

    if (!string.IsNullOrWhiteSpace(configured) &&
        Uri.TryCreate(EnsureTrailingSlash(configured), UriKind.Absolute, out var configuredUri))
    {
        return configuredUri;
    }

    // In Docker Compose the environment sets CommandCenter__GatewayBaseUrl.
    // This local-network fallback keeps the deployed container from routing API
    // calls back into itself if that setting is accidentally omitted.
    if (!IsDevelopment(services))
    {
        return new Uri("http://command-center-gateway:8080/");
    }

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

static bool IsDevelopment(IServiceProvider services)
{
    var environment = services.GetService<IWebHostEnvironment>();
    return environment?.IsDevelopment() == true;
}

static string EnsureTrailingSlash(string value)
{
    return value.Length > 0 && value[^1] == '/' ? value : value + "/";
}
