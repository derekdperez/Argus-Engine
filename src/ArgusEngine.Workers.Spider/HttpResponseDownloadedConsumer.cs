using MassTransit;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Assets;
using ArgusEngine.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.Spider;

public sealed class HttpResponseDownloadedConsumer(
    IEventOutbox outbox,
    ILogger<HttpResponseDownloadedConsumer> logger) : IConsumer<HttpResponseDownloaded>
{
    private const int MaxLinksPerAsset = 250;

    public async Task Consume(ConsumeContext<HttpResponseDownloaded> context)
    {
        var message = context.Message;
        
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["TargetId"] = message.TargetId,
            ["AssetId"] = message.AssetId,
            ["Url"] = message.Snapshot.FinalUrl
        });

        if (message.AssetKind != AssetKind.Url && message.AssetKind != AssetKind.Subdomain && message.AssetKind != AssetKind.Domain)
        {
            return;
        }

        var snapshot = message.Snapshot;
        var body = snapshot.ResponseBody ?? string.Empty;
        var contentType = snapshot.ContentType ?? string.Empty;
        var baseUrl = snapshot.FinalUrl ?? "";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            logger.LogWarning("Could not resolve base URI {BaseUrl} for spidering.", baseUrl);
            return;
        }

        var links = LinkHarvest.Extract(body, contentType, baseUri, MaxLinksPerAsset);
        if (links.Count == 0)
        {
            return;
        }

        var parentPage = baseUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var spiderContext = $"Spider: link extracted from fetched page {parentPage}";
        if (spiderContext.Length > 512) spiderContext = spiderContext[..511] + "…";

        var correlation = message.CorrelationId;
        var now = DateTimeOffset.UtcNow;
        var nextDepth = message.AssetDepth + 1;
        
        var events = new List<AssetDiscovered>(links.Count);

        foreach (var link in links)
        {
            events.Add(
                new AssetDiscovered(
                    message.TargetId,
                    message.RootDomain,
                    message.GlobalMaxDepth,
                    nextDepth,
                    LinkHarvest.GuessKindForUrl(link),
                    link,
                    "spider-worker",
                    now,
                    correlation,
                    AssetAdmissionStage.Raw,
                    null,
                    spiderContext,
                    EventId: NewId.NextGuid(),
                    CausationId: message.EventId,
                    Producer: "worker-spider"));
        }

        await outbox.EnqueueBatchAsync(events, context.CancellationToken).ConfigureAwait(false);
        logger.LogInformation("Extracted {LinkCount} links from {Url}.", links.Count, baseUrl);
    }
}
