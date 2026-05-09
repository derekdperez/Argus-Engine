using ArgusEngine.CommandCenter.Web.Clients;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddRadzenComponents();

builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<DiscoveryRealtimeClient>();

builder.Services.AddHttpClient("GatewayClient", sp => {
    sp.BaseAddress = ResolveGatewayBaseAddress(sp);
});

builder.Services.AddHttpClient<DiscoveryApiClient>("GatewayClient");
builder.Services.AddHttpClient<OperationsApiClient>("GatewayClient");
builder.Services.AddHttpClient<WorkerControlApiClient>("GatewayClient");
builder.Services.AddHttpClient<MaintenanceApiClient>("GatewayClient");
builder.Services.AddHttpClient<UpdatesApiClient>("GatewayClient");
builder.Services.AddHttpClient<RealtimeApiClient>("GatewayClient");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = ResolveRequestBaseAddress(sp) });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.MapStaticAssets();

app.UseStaticFiles();

app.MapGet("/ArgusEngine.CommandCenter.styles.css", () =>
    Results.Redirect("/ArgusEngine.CommandCenter.Web.styles.css", permanent: false));

app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.MapRazorPages();
app.MapBlazorHub();

await app.RunAsync();

static Uri ResolveRequestBaseAddress(IServiceProvider services)
{
    var request = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request;

    if (request is not null)
    {
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : "";

        return new Uri($"{request.Scheme}://{request.Host}{pathBase}/");
    }

    var navigation = services.GetRequiredService<NavigationManager>();

    return new Uri(navigation.BaseUri);
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

