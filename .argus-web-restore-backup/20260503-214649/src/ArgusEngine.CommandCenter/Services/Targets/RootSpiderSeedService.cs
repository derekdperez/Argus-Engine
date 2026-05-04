using MassTransit;
using ArgusEngine.Application.Events;
using ArgusEngine.Contracts.Events;
using AssetAdmissionStage = ArgusEngine.Contracts.AssetAdmissionStage;
using AssetKind = ArgusEngine.Contracts.AssetKind;

namespace ArgusEngine.CommandCenter.Services.Targets;

public sealed class RootSpiderSeedService(IEventOutbox outbox)
{
    public async Task<int> QueueRootSpiderSeedsAsync(
        Guid targetId,
        string rootDomain,
        int globalMaxDepth,
        DateTimeOffset occurredAtUtc,
        Guid correlationId,
        Guid causationId,
        CancellationToken ct)
    {
        var queued = 0;
        foreach (var rootUrl in RootSpiderSeedUrls(rootDomain))
        {
            await outbox.EnqueueAsync(
                    new AssetDiscovered(
                        targetId,
                        rootDomain,
                        globalMaxDepth,
                        0,
                        AssetKind.Url,
                        rootUrl,
                        "command-center-root-seed",
                        occurredAtUtc,
                        correlationId,
                        AssetAdmissionStage.Raw,
                        null,
                        "Target root domain spider seed",
                        EventId: NewId.NextGuid(),
                        CausationId: causationId == Guid.Empty ? correlationId : causationId,
                        Producer: "command-center"),
                    ct)
                .ConfigureAwait(false);
            queued++;
        }

        return queued;
    }

    private static IEnumerable<string> RootSpiderSeedUrls(string rootDomain)
    {
        var host = rootDomain.Trim().Trim('/').TrimEnd('.');
        if (host.Length == 0)
            yield break;

        yield return $"https://{host}/";
        yield return $"http://{host}/";
    }
}
