using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.Assets;
using NightmareV2.Application.Events;
using NightmareV2.Application.TechnologyIdentification;
using NightmareV2.Application.Workers;
using NightmareV2.Contracts;
using NightmareV2.Contracts.Events;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.Workers.TechnologyIdentification.Consumers;

public sealed class TechnologyIdentificationConsumer(
    NightmareDbContext db,
    IWorkerToggleReader toggles,
    IInboxDeduplicator inbox,
    TechnologyCatalog catalog,
    TechnologyScanner scanner,
    HtmlSignalExtractor htmlSignals,
    CookieExtractor cookieExtractor,
    IAssetTagService tagService,
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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var requestHeaders = snapshot.RequestHeaders ?? new Dictionary<string, string>();
            var responseHeaders = snapshot.ResponseHeaders ?? new Dictionary<string, string>();
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
