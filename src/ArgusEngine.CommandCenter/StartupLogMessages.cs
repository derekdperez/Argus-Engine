using Microsoft.Extensions.Logging;

namespace ArgusEngine.CommandCenter;

internal static partial class StartupLogMessages
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Startup database EnsureCreated skipped (Nightmare:SkipStartupDatabase or NIGHTMARE_SKIP_STARTUP_DATABASE=1). APIs that need Postgres will still fail until a database is reachable.")]
    public static partial void StartupDatabaseSkipped(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Startup database initialization completed.")]
    public static partial void StartupDatabaseInitializationCompleted(ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Startup database initialization failed on attempt {Attempt}; retrying.")]
    public static partial void StartupDatabaseInitializationRetry(ILogger logger, Exception exception, int attempt);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Startup database initialization failed after retries. Command Center will continue to serve /health and diagnostics, but database-backed APIs will fail until Postgres/schema is fixed.")]
    public static partial void StartupDatabaseInitializationFailed(ILogger logger, Exception exception);
}
