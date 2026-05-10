using ArgusEngine.CommandCenter.Web.Clients;
using ArgusEngine.CommandCenter.Web.Components;
using Microsoft.AspNetCore.Components;
using DiscoveryRealtimeClient = ArgusEngine.CommandCenter.Realtime.DiscoveryRealtimeClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();

// The Command Center web app talks back to the same host for its local API
// surface. Register these as simple scoped clients to avoid AddHttpClient<T>
// overload ambiguity under the current .NET 10 SDK/compiler.
builder.Services.AddScoped(CreateRequestHttpClient);
builder.Services.AddScoped(sp => new DiscoveryApiClient(CreateRequestHttpClient(sp)));
builder.Services.AddScoped(sp => new OperationsApiClient(CreateRequestHttpClient(sp)));
builder.Services.AddScoped(sp => new WorkerControlApiClient(CreateRequestHttpClient(sp)));
builder.Services.AddScoped(sp => new MaintenanceApiClient(CreateRequestHttpClient(sp)));
builder.Services.AddScoped(sp => new UpdatesApiClient(CreateRequestHttpClient(sp)));
builder.Services.AddScoped(sp => new RealtimeApiClient(CreateRequestHttpClient(sp)));

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
app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);

static HttpClient CreateRequestHttpClient(IServiceProvider services)
{
    return new HttpClient
    {
        BaseAddress = ResolveRequestBaseAddress(services)
    };
}

static Uri ResolveRequestBaseAddress(IServiceProvider services)
{
    var request = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request;
    if (request is not null)
    {
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        return new Uri($"{request.Scheme}://{request.Host}{pathBase}/");
    }

    var navigation = services.GetService<NavigationManager>();
    if (navigation is not null)
    {
        return new Uri(navigation.BaseUri);
    }

    return new Uri("http://localhost/");
}
