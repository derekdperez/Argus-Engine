using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Assets;
using ArgusEngine.Contracts.Assets;
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

    private static readonly Action<ILogger, Guid, Exception?> LogSnapshotError =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(1, nameof(LogSnapshotError)),
            "HighValueRegex: could not deserialize UrlFetchSnapshot for asset {AssetId}");

    private static readonly Action<ILogger, int, Guid, long, Exception?> LogMatchSummary =
        LoggerMessage.Define<int, Guid, long>(
            LogLevel.Information,
            new EventId(2, nameof(LogMatchSummary)),
            "HighValueRegex matched {HitCount} patterns for asset {AssetId} in {ElapsedMs} ms");

    private static readonly Action<ILogger, int, string, Exception?> LogWebhookError =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogWebhookError)),
            "Critical webhook returned {Status} for {Url}");

    private static readonly Action<ILogger, string, Exception?> LogWebhookException =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogWebhookException)),
            "Critical webhook POST failed for {Url}");

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
            LogSnapshotError(logger, message.AssetId, ex);
            return;
        }

        if (snapshot is null)
            return;

        snapshot = await HydrateResponseBodyAsync(snapshot, ct).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var hits = matcher.ScanUrlHttpExchange(message.SourceUrl, snapshot).ToList();

        if (hits.Count == 0)
            return;

        foreach (var hit in hits)
        {
            var severity = hit.ImportanceScore >= 90 ? "Critical" : hit.ImportanceScore >= 50 ? "Medium" : "Low";

            var findingId = await findingWriter.InsertFindingAsync(
                    new HighValueFindingInput(
                        message.TargetId,
                        message.AssetId,
                        "Regex",
                        severity,
                        hit.PatternName,
                        hit.Scope,
                        hit.MatchedSnippet,
                        message.SourceUrl,
                        WorkerKeys.HighValueRegex,
                        hit.ImportanceScore),
                    ct)
                .ConfigureAwait(false);

            if (hit.ImportanceScore >= 90)
            {
                await RaiseCriticalAsync(findingId, message, hit.PatternName, ct).ConfigureAwait(false);
            }

            if (hit.ImportanceScore >= 50)
            {
                await EmitSignalAssetAsync(message, hit, ct).ConfigureAwait(false);
            }
        }

        LogMatchSummary(logger, hits.Count, message.AssetId, stopwatch.ElapsedMilliseconds, null);
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
                LogWebhookError(logger, (int)resp.StatusCode, webhookUrl, null);
        }
        catch (Exception ex)
        {
            LogWebhookException(logger, webhookUrl, ex);
        }
    }
}
