using System.Diagnostics;
using System.Globalization;
using ArgusEngine.CommandCenter.Realtime;
using ArgusEngine.CommandCenter.Web.Clients;
using ArgusEngine.CommandCenter.Web.Components;
using ArgusEngine.CloudDeploy;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = ResolveGatewayBaseAddress(sp) });
builder.Services.AddScoped<WorkerControlApiClient>();
builder.Services.AddScoped<DiscoveryApiClient>();
builder.Services.AddGcpHybridDeploy(builder.Configuration);
builder.Services.AddScoped<DiscoveryRealtimeClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseResponseCompression();

app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();

    context.Response.OnStarting(() =>
    {
        stopwatch.Stop();
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture);
        context.Response.Headers["Server-Timing"] = $"app;dur={elapsedMs}";
        context.Response.Headers["X-Argus-App-Duration-Ms"] = elapsedMs;
        return Task.CompletedTask;
    });

    await next().ConfigureAwait(false);
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Keep the prerendered HTML shell fresh even when static assets are aggressively cached.
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

    await next().ConfigureAwait(false);
});

app.MapStaticAssets();
app.UseStaticFiles();

app.MapGet("/ArgusEngine.CommandCenter.styles.css", () =>
    Results.Redirect("/ArgusEngine.CommandCenter.Web.styles.css", permanent: false));

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
        configuration["CommandCenter:GatewayBaseUrl"]
        ?? configuration["Argus:CommandCenter:GatewayBaseUrl"]
        ?? configuration["CommandCenter:Services:Gateway"]
        ?? configuration["Argus:CommandCenter:Services:Gateway"];

    if (!string.IsNullOrWhiteSpace(configured)
        && Uri.TryCreate(EnsureTrailingSlash(configured), UriKind.Absolute, out var configuredUri))
    {
        return configuredUri;
    }

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
    var environment = services.GetService<IHostEnvironment>();
    return environment?.IsDevelopment() == true;
}

static string EnsureTrailingSlash(string value)
{
    return value.Length > 0 && value[^1] == '/' ? value : value + "/";
}
