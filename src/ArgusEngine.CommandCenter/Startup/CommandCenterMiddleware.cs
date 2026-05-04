using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ArgusEngine.CommandCenter.Security;
using ArgusEngine.Infrastructure.Configuration;

namespace ArgusEngine.CommandCenter.Startup;

public static class CommandCenterMiddleware
{
    public static WebApplication UseCommandCenterMiddleware(this WebApplication app)
    {
        var listenPlainHttp = app.Configuration.GetArgusValue("ListenPlainHttp", false);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            if (!listenPlainHttp)
            {
                app.UseHsts();
            }
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        if (!listenPlainHttp)
        {
            app.UseHttpsRedirection();
        }

        // Preserved current readiness/liveness endpoints.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        // Restored deleted protection for diagnostic/data-maintenance endpoints.
        app.UseSensitiveEndpointProtection();

        // Required for .NET 9+ static web asset endpoint routing and conventional wwwroot assets.
        app.MapStaticAssets();
        app.UseStaticFiles();
        app.UseAntiforgery();

        return app;
    }
}
