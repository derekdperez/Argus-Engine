using ArgusEngine.CommandCenter.Realtime;
using ArgusEngine.CommandCenter.Web.Clients;
using ArgusEngine.CommandCenter.Web.Components;
using ArgusEngine.CommandCenter.Web.Configuration;
using ArgusEngine.CommandCenter.Web.Health;
using ArgusEngine.CloudDeploy;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<CommandCenterWebOptions>()
    .Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
    {
        options.GatewayBaseUrl = ResolveGatewayBaseAddressFromConfiguration(configuration, environment).ToString();
    })
    .Validate(
        options => Uri.TryCreate(options.GatewayBaseUrl, UriKind.Absolute, out _),
        "CommandCenter:GatewayBaseUrl must be configured as an absolute URI.")
    .ValidateOnStart();

builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy("Command Center Web process is live."),
        tags: ["live"])
    .AddCheck<CommandCenterWebReadinessHealthCheck>(
        "command-center-web-readiness",
        tags: ["ready"]);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();

// Register the Radzen services used by the Web UI without relying on the
// AddRadzenComponents extension method being visible to this project at compile time.
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();

// Blazor server components execute on the server. Relative HttpClient calls
// must therefore target the Command Center gateway, not command-center-web
// itself. The gateway owns split-service routing for /api and /hubs paths.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = ResolveGatewayBaseAddress(sp) });

// Client wrappers used by Ops and restored standalone pages.
builder.Services.AddScoped<WorkerControlApiClient>();
builder.Services.AddScoped<DiscoveryApiClient>();

builder.Services.AddGcpHybridDeploy(builder.Configuration);

// CommandCenter and HighValueFindings inject this realtime client directly. Register
// it explicitly so prerender/smoke-test can construct pages before the
// interactive circuit starts.
builder.Services.AddScoped<DiscoveryRealtimeClient>();

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
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
        }

        return Task.CompletedTask;
    });

    await next();
});

app.MapStaticAssets();
app.UseStaticFiles();

// Compatibility alias for clients, proxies, or stale HTML that still request the
// pre-split Command Center CSS isolation bundle name. The current web project
// emits ArgusEngine.CommandCenter.Web.styles.css.
app.MapGet(
    "/ArgusEngine.CommandCenter.styles.css",
    () => Results.Redirect("/ArgusEngine.CommandCenter.Web.styles.css", permanent: false));

app.UseAntiforgery();

app.MapHealthChecks(
        "/health/live",
        new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live")
        })
    .AllowAnonymous();

app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        })
    .AllowAnonymous();

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

    if (!string.IsNullOrWhiteSpace(configured)
        && Uri.TryCreate(EnsureTrailingSlash(configured), UriKind.Absolute, out var configuredUri))
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

static Uri ResolveGatewayBaseAddressFromConfiguration(IConfiguration configuration, IHostEnvironment environment)
{
    var configured =
        configuration["CommandCenter:GatewayBaseUrl"] ??
        configuration["Argus:CommandCenter:GatewayBaseUrl"] ??
        configuration["CommandCenter:Services:Gateway"] ??
        configuration["Argus:CommandCenter:Services:Gateway"];

    if (!string.IsNullOrWhiteSpace(configured)
        && Uri.TryCreate(EnsureTrailingSlash(configured), UriKind.Absolute, out var configuredUri))
    {
        return configuredUri;
    }

    // Do not require an explicit gateway URL in production containers; the split
    // Command Center Compose stack uses this service name, and validating the
    // fallback at startup catches malformed URI/config binding issues without
    // creating a readiness cycle between web and gateway.
    if (!environment.IsDevelopment())
    {
        return new Uri("http://command-center-gateway:8080/");
    }

    return new Uri("http://localhost/");
}

static bool IsDevelopment(IServiceProvider services)
{
    var environment = services.GetService<IHostEnvironment>();
    return environment?.IsDevelopment() == true;
}

static string EnsureTrailingSlash(string value)
{
    return value.Length > 0 && value[^1] == '/' ? value : value + "/";
}
