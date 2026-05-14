using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Services;

public sealed class CoverageAutomationBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IOptions<CoverageAutomationOptions> options,
    ILogger<CoverageAutomationBackgroundService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, DateTimeOffset> _lastEnumerationQueuedAt = new();
    private readonly object _enumerationLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Coverage automation disabled.");
            return;
        }

        var initialDelaySeconds = Math.Clamp(settings.InitialDelaySeconds, 0, 3600);
        if (initialDelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds), stoppingToken).ConfigureAwait(false);
        }

        logger.LogInformation("Coverage automation started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(settings, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Coverage automation tick failed.");
            }

            var intervalSeconds = Math.Clamp(settings.IntervalSeconds, 5, 3600);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            settings = options.Value;
        }
    }

    private async Task RunTickAsync(CoverageAutomationOptions settings, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IEventOutbox>();
        var subdomainOptions = scope.ServiceProvider.GetRequiredService<IOptions<SubdomainEnumerationOptions>>();

        var now = DateTimeOffset.UtcNow;
        var providers = ResolveProviders(subdomainOptions.Value.DefaultProviders);

        var queuedEnumJobs = await QueueMissingEnumerationAsync(
                db,
                outbox,
                providers,
                settings,
                now,
                ct)
            .ConfigureAwait(false);

        var queuedSpiderJobs = await QueueMissingSpiderWorkAsync(db, settings, now, ct).ConfigureAwait(false);

        if (settings.EnsureWorkersAvailable)
        {
            if (queuedEnumJobs > 0)
            {
                await EnsureWorkersAvailableAsync(
                        ct,
                        ("worker-enum", Math.Max(1, settings.EnumerationWorkerMinimumCount)))
                    .ConfigureAwait(false);
            }

            if (queuedSpiderJobs > 0)
            {
                await EnsureWorkersAvailableAsync(
                        ct,
                        ("worker-spider", Math.Max(1, settings.SpiderWorkerMinimumCount)),
                        ("worker-http-requester", Math.Max(1, settings.HttpRequesterWorkerMinimumCount)))
                    .ConfigureAwait(false);
            }
        }

        if (queuedEnumJobs > 0 || queuedSpiderJobs > 0)
        {
            logger.LogInformation(
                "Coverage automation queued work. EnumerationJobs={EnumerationJobs}, SpiderQueueRows={SpiderQueueRows}",
                queuedEnumJobs,
                queuedSpiderJobs);
        }
    }

    private async Task<int> QueueMissingEnumerationAsync(
        ArgusDbContext db,
        IEventOutbox outbox,
        IReadOnlyList<string> providers,
        CoverageAutomationOptions settings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (providers.Count == 0)
        {
            return 0;
        }

        var batchSize = Math.Clamp(settings.EnumerationBatchSize, 1, 10_000);
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(settings.EnumerationRetryMinutes, 1, 7 * 24 * 60));

        var candidates = await db.Targets.AsNoTracking()
            .Where(t => !string.IsNullOrWhiteSpace(t.RootDomain))
            .Where(t => !db.Assets.Any(a => a.TargetId == t.Id && a.Kind == AssetKind.Subdomain))
            .OrderBy(t => t.CreatedAtUtc)
            .Take(batchSize * 4)
            .Select(t => new { t.Id, t.RootDomain })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var eligible = new List<(Guid TargetId, string RootDomain)>(batchSize);

        lock (_enumerationLock)
        {
            PruneEnumerationHistory(now, cooldown);

            foreach (var candidate in candidates)
            {
                if (_lastEnumerationQueuedAt.TryGetValue(candidate.Id, out var lastQueuedAt) && now - lastQueuedAt < cooldown)
                {
                    continue;
                }

                eligible.Add((candidate.Id, candidate.RootDomain));
                if (eligible.Count >= batchSize)
                {
                    break;
                }
            }
        }

        if (eligible.Count == 0)
        {
            return 0;
        }

        var eventsToEnqueue = new List<SubdomainEnumerationRequested>(eligible.Count * providers.Count);

        foreach (var target in eligible)
        {
            var correlation = NewId.NextGuid();

            foreach (var provider in providers)
            {
                eventsToEnqueue.Add(
                    new SubdomainEnumerationRequested(
                        target.TargetId,
                        target.RootDomain,
                        provider,
                        "coverage-automation",
                        now,
                        correlation,
                        EventId: NewId.NextGuid(),
                        CausationId: correlation,
                        Producer: "worker-control-coverage-automation"));
            }
        }

        await outbox.EnqueueBatchAsync(eventsToEnqueue, ct).ConfigureAwait(false);

        lock (_enumerationLock)
        {
            foreach (var target in eligible)
            {
                _lastEnumerationQueuedAt[target.TargetId] = now;
            }
        }

        return eventsToEnqueue.Count;
    }

    private static async Task<int> QueueMissingSpiderWorkAsync(
        ArgusDbContext db,
        CoverageAutomationOptions settings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var batchSize = Math.Clamp(settings.SpiderBatchSize, 1, 20_000);

        var candidates = await db.Assets.AsNoTracking()
            .Where(a => a.Kind == AssetKind.Subdomain)
            .Where(a => !string.IsNullOrWhiteSpace(a.RawValue))
            .Where(a => !db.HttpRequestQueue.Any(q => q.AssetId == a.Id))
            .OrderBy(a => a.DiscoveredAtUtc)
            .Take(batchSize)
            .Select(a => new { a.Id, a.TargetId, a.RawValue })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var queueRows = new List<HttpRequestQueueItem>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (!TryResolveSubdomainRequest(candidate.RawValue, out var requestUrl, out var domainKey))
            {
                continue;
            }

            queueRows.Add(
                new HttpRequestQueueItem
                {
                    Id = Guid.NewGuid(),
                    AssetId = candidate.Id,
                    TargetId = candidate.TargetId,
                    AssetKind = AssetKind.Subdomain,
                    Method = "GET",
                    RequestUrl = requestUrl,
                    DomainKey = domainKey,
                    State = HttpRequestQueueState.Queued,
                    Priority = 10,
                    AttemptCount = 0,
                    MaxAttempts = 3,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    NextAttemptAtUtc = now,
                });
        }

        if (queueRows.Count == 0)
        {
            return 0;
        }

        db.HttpRequestQueue.AddRange(queueRows);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another worker may have inserted concurrent queue rows; this tick
            // is best-effort and will reconcile on the next pass.
            return 0;
        }

        return queueRows.Count;
    }

    private static IReadOnlyList<string> ResolveProviders(string[] configuredProviders)
    {
        var providers = configuredProviders
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (providers.Count == 0)
        {
            providers.Add("subfinder");
            providers.Add("amass");
        }

        return providers;
    }

    private static bool TryResolveSubdomainRequest(string rawValue, out string requestUrl, out string domainKey)
    {
        requestUrl = "";
        domainKey = "";

        var candidate = rawValue.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
        {
            candidate = absolute.Host;
        }
        else
        {
            var slash = candidate.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                candidate = candidate[..slash];
            }

            var colon = candidate.LastIndexOf(':');
            if (colon > -1 && candidate.Count(c => c == ':') == 1)
            {
                candidate = candidate[..colon];
            }
        }

        candidate = candidate.Trim().TrimEnd('.').ToLowerInvariant();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (!Uri.TryCreate($"https://{candidate}/", UriKind.Absolute, out var requestUri))
        {
            return false;
        }

        requestUrl = requestUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        domainKey = requestUri.IdnHost.ToLowerInvariant();
        return true;
    }

    private async Task EnsureWorkersAvailableAsync(
        CancellationToken ct,
        params (string ServiceName, int MinimumCount)[] requiredWorkers)
    {
        try
        {
            var counts = await DockerComposeWorkerScaler
                .GetRunningServiceCountsAsync(configuration, logger, ct)
                .ConfigureAwait(false);

            foreach (var worker in requiredWorkers)
            {
                counts.TryGetValue(worker.ServiceName, out var running);
                if (running >= worker.MinimumCount)
                {
                    continue;
                }

                await DockerComposeWorkerScaler
                    .ScaleWorkerAsync(worker.ServiceName, worker.MinimumCount, configuration, logger, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Coverage automation could not verify/scale worker containers.");
        }
    }

    private void PruneEnumerationHistory(DateTimeOffset now, TimeSpan cooldown)
    {
        if (_lastEnumerationQueuedAt.Count < 10_000)
        {
            return;
        }

        var threshold = now - cooldown - cooldown;
        var expired = _lastEnumerationQueuedAt
            .Where(kvp => kvp.Value < threshold)
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (var key in expired)
        {
            _lastEnumerationQueuedAt.Remove(key);
        }
    }
}
