using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Startup;

public static class StartupDatabaseInitializer
{
    public static async Task InitializeCommandCenterDatabasesAsync(this WebApplication app)
    {
        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        if (ShouldSkipStartupDatabase(app.Configuration))
        {
            startupLog.LogInformation("Skipping startup database bootstrap for command-center.");
            return;
        }

        var continueOnFailure = app.Configuration.GetArgusValue(
            "ContinueOnStartupDatabaseFailure",
            app.Environment.IsDevelopment());

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
                await ArgusDbBootstrap.InitializeAsync(
                    app.Services,
                    app.Configuration,
                    startupLog,
                    includeFileStore: true,
                    app.Lifetime.ApplicationStopping)
                    .ConfigureAwait(false);

                startupLog.LogInformation("Command-center startup database bootstrap completed.");
                return;
            }
            catch (Exception ex) when (attempt <= retryDelays.Length && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                startupLog.LogWarning(ex, "Retrying command-center startup database bootstrap. Attempt {Attempt}.", attempt);
                await Task.Delay(retryDelays[attempt - 1], app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                startupLog.LogCritical(
                    ex,
                    "Command Center startup database initialization failed for the application and file-store databases. ContinueOnStartupDatabaseFailure={ContinueOnStartupDatabaseFailure}.",
                    continueOnFailure);

                if (!continueOnFailure)
                {
                    throw;
                }

                startupLog.LogError(ex, "Command-center startup database bootstrap failed, continuing startup.");
                return;
            }
        }
    }

    private static bool ShouldSkipStartupDatabase(IConfiguration configuration)
    {
        var configuredSkip =
            configuration["Argus:SkipStartupDatabase"]
            ?? configuration["ARGUS_SKIP_STARTUP_DATABASE"]
            ?? Environment.GetEnvironmentVariable("ARGUS_SKIP_STARTUP_DATABASE");

        return string.Equals(configuredSkip, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredSkip, "1", StringComparison.OrdinalIgnoreCase);
    }
}
