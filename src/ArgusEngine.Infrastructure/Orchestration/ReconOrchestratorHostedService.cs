using ArgusEngine.Application.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Infrastructure.Orchestration;

public sealed class ReconOrchestratorHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconOrchestratorOptions> options,
    ILogger<ReconOrchestratorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var owner = $"recon-orchestrator-{Environment.MachineName}";

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 5, 3600));

            try
            {
                if (options.Value.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<IReconOrchestrator>();
                    var targetIds = await orchestrator.GetActiveTargetIdsAsync(stoppingToken).ConfigureAwait(false);

                    foreach (var targetId in targetIds)
                    {
                        var result = await orchestrator.TickTargetAsync(targetId, owner, stoppingToken).ConfigureAwait(false);
                        if (result.Claimed)
                        {
                            logger.LogDebug(
                                "Recon orchestrator tick completed for target {TargetId}. ProvidersQueued={ProvidersQueued}, SubdomainsChecked={SubdomainsChecked}, SeedsQueued={SeedsQueued}, IncompleteSubdomains={IncompleteSubdomains}, Completed={Completed}",
                                targetId,
                                result.ProvidersQueued,
                                result.SubdomainsChecked,
                                result.SubdomainSeedsQueued,
                                result.IncompleteSubdomains,
                                result.Completed);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Recon orchestrator background tick failed.");
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
