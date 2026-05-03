using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.FileStore;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Workers.TechnologyIdentification.Consumers;

public sealed class TechnologyIdentificationConsumer(
    ArgusDbContext db,
    IWorkerToggleReader toggles,
    IInboxDeduplicator inbox,
    TechnologyCatalog catalog,
    TechnologyScanner scanner,
    HtmlSignalExtractor htmlSignals,
    CookieExtractor cookieExtractor,
    IAssetTagService tagService,
    IHttpArtifactReader artifactReader,
    IOptions<TechnologyIdentificationScanOptions> scanOptions,
    ILogger<TechnologyIdentificationConsumer> logger) : IConsumer<ScannableContentAvailable>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            logger.LogDebug(ex, "TechnologyIdentification: could not deserialize UrlFetchSnapshot for asset {AssetId}", message.AssetId);
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
            var signals = htmlSignals.Extract(snapshot.ResponseBody, snapshot.ContentType, snapshot.FinalUrl ?? message.SourceUrl);
            var cookies = cookieExtractor.Extract(requestHeaders, responseHeaders);
            var scriptUrls = BuildScriptUrls(message.SourceUrl, snapshot.FinalUrl, snapshot.ContentType, signals.ScriptUrls);

            var input = new TechnologyScanInput(
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

            var results = scanner.Scan(input);
            var persisted = await tagService.UpsertTechnologyDetectionsAsync(
                    message.TargetId,
                    message.AssetId,
                    results,
                    catalog.Technologies,
                    ct)
                .ConfigureAwait(false);

            logger.LogInformation(
                "TechnologyIdentification scanned asset {AssetId} in {ElapsedMs} ms: matches={MatchCount}, technologies={TechnologyCount}, tagsAttached={TagsAttached}",
                message.AssetId,
                stopwatch.ElapsedMilliseconds,
                results.Count,
                persisted.TechnologyCount,
                persisted.TagsAttached);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TechnologyIdentification scan failed for asset {AssetId}", message.AssetId);
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

    private static IReadOnlyList<string> BuildScriptUrls(
        string sourceUrl,
        string? finalUrl,
        string? contentType,
        IReadOnlyList<string> extractedScriptUrls)
    {
        var scripts = extractedScriptUrls.ToList();

        if (LooksLikeJavaScript(contentType, finalUrl ?? sourceUrl))
        {
            scripts.Add(sourceUrl);
            if (!string.IsNullOrWhiteSpace(finalUrl))
                scripts.Add(finalUrl);
        }

        return scripts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
