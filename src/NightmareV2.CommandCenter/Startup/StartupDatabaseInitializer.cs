using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Startup;

public static class StartupDatabaseInitializer
{
    public static async Task InitializeCommandCenterDatabasesAsync(this WebApplication app)
    {
        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        if (ShouldSkipStartupDatabase(app.Configuration))
        {
            StartupLogMessages.StartupDatabaseSkipped(startupLog);
            return;
        }

        var continueOnFailure = app.Configuration.GetValue("Nightmare:ContinueOnStartupDatabaseFailure", true);
        var retryDelays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(15),
        };

        for (var attempt = 1; attempt <= retryDelays.Length + 1; attempt++)
        {
            try
            {
                await StartupDatabaseBootstrap.InitializeAsync(
                        app.Services,
                        app.Configuration,
                        startupLog,
                        includeFileStore: true,
                        app.Lifetime.ApplicationStopping)
                    .ConfigureAwait(false);
                StartupLogMessages.StartupDatabaseInitializationCompleted(startupLog);
                return;
            }
            catch (Exception ex) when (attempt <= retryDelays.Length && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                StartupLogMessages.StartupDatabaseInitializationRetry(startupLog, ex, attempt);
                await Task.Delay(retryDelays[attempt - 1], app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                if (!continueOnFailure)
                    throw;

                StartupLogMessages.StartupDatabaseInitializationFailed(startupLog, ex);
                return;
            }
        }
    }

    private static bool ShouldSkipStartupDatabase(IConfiguration configuration)
    {
        var configuredSkip =
            configuration["Nightmare:SkipStartupDatabase"]
            ?? configuration["NIGHTMARE_SKIP_STARTUP_DATABASE"];

        if (string.Equals(configuredSkip, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredSkip, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Environment.GetEnvironmentVariable("NIGHTMARE_SKIP_STARTUP_DATABASE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }
}
