using CliWrap;

namespace ArgusEngine.Application.Workers;

public abstract class BaseCliWorker
{
    protected async Task<CommandResult> RunCliToolAsync(
        string executable,
        string[] arguments,
        CancellationToken contextToken,
        TimeSpan hardTimeout)
    {
        using var timeoutCts = new CancellationTokenSource(hardTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(contextToken, timeoutCts.Token);

        try
        {
            var result = await Cli.Wrap(executable)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(linkedCts.Token);

            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"The execution of '{executable} {string.Join(" ", arguments)}' timed out after {hardTimeout.TotalSeconds} seconds.");
        }
    }
}
