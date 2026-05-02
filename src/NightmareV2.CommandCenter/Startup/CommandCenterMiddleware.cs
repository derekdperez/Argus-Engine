namespace NightmareV2.CommandCenter.Startup;

public static class CommandCenterMiddleware
{
    public static WebApplication UseCommandCenterMiddleware(this WebApplication app)
    {
        var listenPlainHttp = app.Configuration.GetValue("Nightmare:ListenPlainHttp", false);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            if (!listenPlainHttp)
                app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        if (!listenPlainHttp)
            app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseAntiforgery();

        return app;
    }
}
