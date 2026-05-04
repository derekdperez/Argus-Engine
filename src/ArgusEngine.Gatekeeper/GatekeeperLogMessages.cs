using Microsoft.Extensions.Logging;

namespace ArgusEngine.Gatekeeper;

internal static partial class GatekeeperLogMessages
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Gatekeeper startup database bootstrap failed on attempt {Attempt}; retrying.")]
    public static partial void DatabaseBootstrapRetry(ILogger logger, Exception exception, int attempt);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Critical,
        Message = "Gatekeeper startup database bootstrap failed. ContinueOnStartupDatabaseFailure={ContinueOnStartupDatabaseFailure}.")]
    public static partial void DatabaseBootstrapFailed(ILogger logger, Exception exception, bool continueOnStartupDatabaseFailure);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Gatekeeper startup database bootstrap completed.")]
    public static partial void DatabaseBootstrapCompleted(ILogger logger);
}
