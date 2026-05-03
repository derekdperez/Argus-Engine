using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.FileStore;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Workers.HighValue.Consumers;

public sealed class HighValueRegexConsumer(
    IDbContextFactory<ArgusDbContext> dbFactory,
    IWorkerToggleReader toggles,
    IInboxDeduplicator inbox,
    IEventOutbox outbox,
    HighValueRegexMatcher matcher,
    IHighValueFindingWriter findingWriter,
    IHttpArtifactReader artifactReader,
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    IOptions<HighValueScanOptions> scanOptions,
    ILogger<HighValueRegexConsumer> logger) : IConsumer<ScannableContentAvailable>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task Consume(ConsumeContext<ScannableContentAvailable> context)
    {
        var ct = context.CancellationToken;

        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(HighValueRegexConsumer), ct).ConfigureAwait(false))
            return;

        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.HighValueRegex, ct).ConfigureAwait(false))
            return;

        var message = context.Message;
        if (message.Source != ScannableContentSource.UrlHttpResponse)
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var asset = await db.Assets.AsNoTracking()
            .Where(a => a.Id == message.AssetId)
            .Select(a => new { a.TypeDetailsJson })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (asset?.TypeDetailsJson is not { Length: > 0 } json)
            return;

        UrlFetchSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<UrlFetchSnapshot>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HighValueRegex: could not deserialize UrlFetchSnapshot for asset {AssetId}", message.AssetId);
            return;
        }

        if (snapshot is null)
            return;

        snapshot = await HydrateResponseBodyAsync(snapshot, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(snapshot.ResponseBody))
            return;

        var stopwatch = Stopwatch.StartNew();
        var hits = matcher.Match(snapshot.ResponseBody);

        if (hits.Count == 0)
            return;

        foreach (var hit in hits)
        {
            var findingId = await findingWriter.WriteAsync(
                    new HighValueFindingInput(
                        message.TargetId,
                        message.AssetId,
                        hit.PatternName,
                        hit.MatchedSnippet,
                        hit.Confidence,
                        hit.Scope,
                        message.SourceUrl,
                        DateTimeOffset.UtcNow),
                    ct)
                .ConfigureAwait(false);

            if (hit.Confidence >= 0.9)
            {
                await RaiseCriticalAsync(findingId, message, hit.PatternName, ct).ConfigureAwait(false);
            }

            if (hit.Confidence >= 0.5)
            {
                await EmitSignalAssetAsync(message, hit, ct).ConfigureAwait(false);
            }
        }

        logger.LogInformation(
            "HighValueRegex matched {HitCount} patterns for asset {AssetId} in {ElapsedMs} ms",
            hits.Count,
            message.AssetId,
            stopwatch.ElapsedMilliseconds);
    }

    private async Task<UrlFetchSnapshot> HydrateResponseBodyAsync(UrlFetchSnapshot snap, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(snap.ResponseBody))
            return snap;

        string? body = null;
        if (snap.ResponseBodyBlobId is { } blobId)
        {
            body = await artifactReader.ReadTextAsync(
                    blobId,
                    scanOptions.Value.MaxResponseBodyScanBytes,
                    ct)
                .ConfigureAwait(false);
        }

        body ??= snap.ResponseBodyPreview;

        return snap with { ResponseBody = body };
    }

    private async Task EmitSignalAssetAsync(
        ScannableContentAvailable message,
        RegexMatchHit hit,
        CancellationToken ct)
    {
        var kind = ClassifySignalKind(hit);
        var rawValue = kind switch
        {
            AssetKind.Secret => $"{hit.PatternName}:{hit.MatchedSnippet}",
            AssetKind.CloudBucket => $"unknown:{hit.MatchedSnippet}",
            _ => hit.MatchedSnippet,
        };

        await outbox.EnqueueAsync(
            new AssetDiscovered(
                message.TargetId,
                "",
                64,
                0,
                kind,
                rawValue,
                WorkerKeys.HighValueRegex,
                DateTimeOffset.UtcNow,
                message.CorrelationId,
                AssetAdmissionStage.Raw,
                AssetId: null,
                DiscoveryContext: $"High-value regex {hit.PatternName} extracted from {message.SourceUrl}",
                ParentAssetId: message.AssetId,
                RelationshipType: AssetRelationshipType.ExtractedFrom,
                IsPrimaryRelationship: false,
                EventId: NewId.NextGuid(),
                CausationId: message.EventId == Guid.Empty ? message.CorrelationId : message.EventId,
                Producer: "worker-highvalue-regex"),
            ct)
            .ConfigureAwait(false);
    }

    private static AssetKind ClassifySignalKind(RegexMatchHit hit)
    {
        var text = $"{hit.PatternName} {hit.Scope} {hit.MatchedSnippet}";
        if (text.Contains('@', StringComparison.Ordinal) && text.Contains('.', StringComparison.Ordinal))
            return AssetKind.Email;

        if (text.Contains("bucket", StringComparison.OrdinalIgnoreCase)
            || text.Contains("s3", StringComparison.OrdinalIgnoreCase)
            || text.Contains("blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
            || text.Contains("storage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return AssetKind.CloudBucket;
        }

        return AssetKind.Secret;
    }

    private async Task RaiseCriticalAsync(
        Guid findingId,
        ScannableContentAvailable m,
        string patternName,
        CancellationToken ct)
    {
        await outbox.EnqueueAsync(
            new CriticalHighValueFindingAlert(
                findingId,
                m.TargetId,
                m.AssetId,
                patternName,
                m.SourceUrl,
                "Critical",
                DateTimeOffset.UtcNow,
                m.CorrelationId,
                EventId: NewId.NextGuid(),
                CausationId: m.EventId == Guid.Empty ? m.CorrelationId : m.EventId,
                Producer: "worker-highvalue-regex"),
            ct)
            .ConfigureAwait(false);

        var webhookUrl = configuration["HighValue:CriticalWebhookUrl"]?.Trim();
        if (string.IsNullOrEmpty(webhookUrl))
            return;

        try
        {
            var client = httpFactory.CreateClient();
            var payload = new
            {
                findingId,
                m.TargetId,
                m.AssetId,
                patternName,
                m.SourceUrl,
                severity = "Critical",
                atUtc = DateTimeOffset.UtcNow,
            };

            using var resp = await client.PostAsJsonAsync(webhookUrl, payload, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Critical webhook returned {Status} for {Url}", (int)resp.StatusCode, webhookUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Critical webhook POST failed for {Url}", webhookUrl);
        }
    }
}
