using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        app.UseStaticFiles();
        app.UseAntiforgery();

        return app;
    }
}
