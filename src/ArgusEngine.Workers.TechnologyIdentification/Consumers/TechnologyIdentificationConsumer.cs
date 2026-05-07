using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.FileStore;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Application.TechnologyIdentification.Fingerprints;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Workers.TechnologyIdentification.Consumers;

public sealed class TechnologyIdentificationConsumer(
    IDbContextFactory<ArgusDbContext> dbFactory,
    IInboxDeduplicator inbox,
    IWorkerToggleReader toggles,
    PassiveTechnologyFingerprintEngine passiveEngine,
    ITechnologyFingerprintCatalog catalog,
    ITechnologyObservationWriter observationWriter,
    IHttpArtifactReader artifactReader,
    IOptions<TechnologyIdentificationScanOptions> scanOptions,
    ILogger<TechnologyIdentificationConsumer> logger) : IConsumer<ScannableContentAvailable>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly Action<ILogger, Guid, Exception?> LogSnapshotError =
        LoggerMessage.Define<Guid>(
            LogLevel.Debug,
            new EventId(1, nameof(LogSnapshotError)),
            "TechnologyIdentification: could not deserialize UrlFetchSnapshot for asset {AssetId}");

    private static readonly Action<ILogger, Guid, long, int, int, int, Exception?> LogScanSummary =
        LoggerMessage.Define<Guid, long, int, int, int>(
            LogLevel.Information,
            new EventId(2, nameof(LogScanSummary)),
            "TechnologyIdentification scanned asset {AssetId} in {ElapsedMs} ms: observations={ObservationCount}, created={CreatedCount}, evidenceAdded={EvidenceAddedCount}");

    private static readonly Action<ILogger, Guid, Exception?> LogScanFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(3, nameof(LogScanFailed)),
            "TechnologyIdentification scan failed for asset {AssetId}");

    public async Task Consume(ConsumeContext<ScannableContentAvailable> context)
    {
        var ct = context.CancellationToken;

        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(TechnologyIdentificationConsumer), ct).ConfigureAwait(false))
            return;

        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.TechnologyIdentification, ct).ConfigureAwait(false))
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

        try
        {
            var requestHeaders = snapshot.RequestHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var responseHeaders = snapshot.ResponseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var signals = HtmlSignalExtractor.Extract(snapshot.ResponseBody, snapshot.ContentType, snapshot.FinalUrl ?? message.SourceUrl);
            var cookies = CookieExtractor.Extract(requestHeaders, responseHeaders);
            var scriptUrls = BuildScriptUrls(message.SourceUrl, snapshot.FinalUrl, snapshot.ContentType, signals.ScriptUrls);

            var input = new PassiveTechnologyFingerprintInput(
                message.TargetId,
                message.AssetId,
                message.SourceUrl,
                snapshot.FinalUrl,
                new Dictionary<string, string>(responseHeaders, StringComparer.OrdinalIgnoreCase),
                snapshot.ResponseBody,
                snapshot.ContentType,
                cookies,
                signals.Meta,
                scriptUrls);

            var observations = passiveEngine.Evaluate(input);
            var persisted = await observationWriter.UpsertPassiveObservationsAsync(
                    message.TargetId,
                    observations,
                    catalog.CatalogHash,
                    ct)
                .ConfigureAwait(false);

            LogScanSummary(
                logger,
                message.AssetId,
                stopwatch.ElapsedMilliseconds,
                observations.Count,
                persisted.CreatedCount,
                persisted.EvidenceAddedCount,
                null);
        }
        catch (Exception ex)
        {
            LogScanFailed(logger, message.AssetId, ex);
        }
    }

    private async Task<UrlFetchSnapshot> HydrateResponseBodyAsync(UrlFetchSnapshot snapshot, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(snapshot.ResponseBody))
            return snapshot;

        string? body = null;

        if (snapshot.ResponseBodyBlobId is { } blobId)
        {
            body = await artifactReader.ReadTextAsync(
                    blobId,
                    scanOptions.Value.MaxResponseBodyScanBytes,
                    ct)
                .ConfigureAwait(false);
        }

        body ??= snapshot.ResponseBodyPreview;

        return snapshot with { ResponseBody = body };
    }

    private static string[] BuildScriptUrls(
        string sourceUrl,
        string? finalUrl,
        string? contentType,
        IReadOnlyList<string> extractedScriptUrls)
    {
        var scripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < extractedScriptUrls.Count; i++)
        {
            var url = extractedScriptUrls[i];
            if (!string.IsNullOrWhiteSpace(url))
                scripts.Add(url);
        }

        if (LooksLikeJavaScript(contentType, finalUrl ?? sourceUrl))
        {
            if (!string.IsNullOrWhiteSpace(sourceUrl))
                scripts.Add(sourceUrl);

            if (!string.IsNullOrWhiteSpace(finalUrl))
                scripts.Add(finalUrl);
        }

        return scripts.Count == 0 ? [] : scripts.ToArray();
    }

    private static bool LooksLikeJavaScript(string? contentType, string url)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && (contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    }
}
