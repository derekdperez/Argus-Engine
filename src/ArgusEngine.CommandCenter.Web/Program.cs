using ArgusEngine.CommandCenter.Components;
using ArgusEngine.CommandCenter.Realtime;
using Microsoft.AspNetCore.Components;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<DiscoveryRealtimeClient>();
builder.Services.AddScoped(
    sp =>
    {
        var navigation = sp.GetRequiredService<NavigationManager>();
        return new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
    });

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
