using System;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Components.QuickGrid;
using NightmareV2.CommandCenter.Components.DataGrid;
using NightmareV2.CommandCenter.Models;

namespace NightmareV2.CommandCenter.Components.Pages;

public partial class Ops
{
    private static readonly GridSort<TargetSummary> SortTargetRoot =
        GridSort<TargetSummary>.ByAscending(static r => r.RootDomain);

    private static readonly GridSort<TargetSummary> SortTargetCreated =
        GridSort<TargetSummary>.ByDescending(static r => r.CreatedAtUtc);

    private static readonly GridSort<AssetGridRowDto> SortAssetDiscoveryContext =
        GridSort<AssetGridRowDto>.ByAscending(static a => a.DiscoveryContext);

    private static readonly GridSort<WorkerKindSummaryDto> SortWorkerKey =
        GridSort<WorkerKindSummaryDto>.ByAscending(static w => w.WorkerKey);

    private static readonly GridSort<WorkerKindSummaryDto> SortWorkerEnabled =
        GridSort<WorkerKindSummaryDto>.ByDescending(static w => w.ToggleEnabled);

    private static readonly GridSort<WorkerKindSummaryDto> SortWorkerLastActivity =
        GridSort<WorkerKindSummaryDto>.ByDescending(static w => w.LastActivityUtc);

    private IQueryable<TargetSummary> FilteredOpsTargets =>
        _targets.AsQueryable().Where(r =>
            GridTextFilter.Matches(r.RootDomain, _targetRootFilter)
            && GridTextFilter.Matches(r.GlobalMaxDepth.ToString(CultureInfo.InvariantCulture), _targetDepthFilter));

    private Func<TargetSummary, string>? TargetGroupKeySelector =>
        _targetGroupBy switch
        {
            "Depth" => static r => $"Depth {r.GlobalMaxDepth}",
            "Created" => static r => r.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => null,
        };

    private static bool TargetRowMatches(TargetSummary r, string q) =>
        GridTextFilter.Matches(r.RootDomain, q)
        || GridTextFilter.Matches(r.Id.ToString(), q)
        || GridTextFilter.Matches(r.GlobalMaxDepth.ToString(CultureInfo.InvariantCulture), q);

    private IQueryable<HttpRequestQueueRowDto> RequestQueueRows =>
        _requestQueue.AsQueryable()
            .OrderByDescending(q => q.CreatedAtUtc);

    private IQueryable<AssetGridRowDto> FilteredAssets =>
        _assets.AsQueryable().Where(a =>
            (GridTextFilter.Matches(a.Kind, _filterAssets)
             || GridTextFilter.Matches(a.LifecycleStatus, _filterAssets)
             || GridTextFilter.Matches(a.RawValue, _filterAssets)
             || GridTextFilter.Matches(a.DiscoveredBy, _filterAssets)
             || GridTextFilter.Matches(a.DiscoveryContext, _filterAssets)
             || GridTextFilter.Matches(a.CanonicalKey, _filterAssets))
            && GridTextFilter.Matches(a.Kind, _filterKindCol)
            && GridTextFilter.Matches(a.LifecycleStatus, _filterStatusCol)
            && (GridTextFilter.Matches(a.RawValue, _filterRawCol)
                || GridTextFilter.Matches(a.CanonicalKey, _filterRawCol))
            && GridTextFilter.Matches(a.DiscoveredBy, _filterPipelineCol)
            && GridTextFilter.Matches(a.DiscoveryContext, _filterHowFoundCol));

    private Func<AssetGridRowDto, string>? AssetGroupKeySelector =>
        _assetGroupBy switch
        {
            "Kind" => static a => string.IsNullOrWhiteSpace(a.Kind) ? "Unknown kind" : a.Kind,
            "Status" => static a => string.IsNullOrWhiteSpace(a.LifecycleStatus) ? "Unknown status" : a.LifecycleStatus,
            "Pipeline" => static a => string.IsNullOrWhiteSpace(a.DiscoveredBy) ? "Unknown pipeline" : a.DiscoveredBy,
            "Depth" => static a => $"Depth {a.Depth}",
            _ => null,
        };

    private IQueryable<HttpRequestQueueRowDto> FilteredRequestQueue =>
        RequestQueueRows.Where(q =>
            (GridTextFilter.Matches(q.AssetKind, _filterQueueSearch)
             || GridTextFilter.Matches(q.RequestUrl, _filterQueueSearch)
             || GridTextFilter.Matches(q.DomainKey, _filterQueueSearch)
             || GridTextFilter.Matches(q.State, _filterQueueSearch)
             || GridTextFilter.Matches(
                 q.LastHttpStatus != null
                     ? q.LastHttpStatus.Value.ToString(CultureInfo.InvariantCulture)
                     : string.Empty,
                 _filterQueueSearch)
             || GridTextFilter.Matches(q.LastError, _filterQueueSearch))
            && GridTextFilter.Matches(q.AssetKind, _filterQueueKindCol)
            && (GridTextFilter.Matches(q.RequestUrl, _filterQueueRawCol)
                || GridTextFilter.Matches(q.DomainKey, _filterQueueRawCol))
            && GridTextFilter.Matches(q.State, _filterQueuePipelineCol));

    private Func<HttpRequestQueueRowDto, string>? QueueGroupKeySelector =>
        _queueGroupBy switch
        {
            "State" => static q => string.IsNullOrWhiteSpace(q.State) ? "Unknown state" : q.State,
            "Kind" => static q => string.IsNullOrWhiteSpace(q.AssetKind) ? "Unknown kind" : q.AssetKind,
            "Domain" => static q => string.IsNullOrWhiteSpace(q.DomainKey) ? "No domain" : q.DomainKey,
            "HTTP" => static q => q.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "No status",
            _ => null,
        };

    private IQueryable<WorkerKindSummaryDto> WorkerRows =>
        (_snapshot?.WorkerActivity.Summaries ?? Array.Empty<WorkerKindSummaryDto>())
            .AsQueryable();

    private IQueryable<RabbitQueueBriefDto> RabbitQueueRows =>
        (_snapshot?.RabbitQueues ?? Array.Empty<RabbitQueueBriefDto>())
            .AsQueryable();

    private static bool WorkerRowMatches(WorkerKindSummaryDto r, string q) =>
        GridTextFilter.Matches(r.WorkerKey, q)
        || GridTextFilter.Matches(r.RollupActivityLabel, q)
        || GridTextFilter.Matches(r.LastActivityUtc?.ToString("u", CultureInfo.InvariantCulture), q);

    private static bool RabbitRowMatches(RabbitQueueBriefDto r, string q) =>
        GridTextFilter.Matches(r.Name, q)
        || GridTextFilter.Matches(r.LikelyWorkerKey, q)
        || GridTextFilter.Matches(r.MessagesReady.ToString(CultureInfo.InvariantCulture), q)
        || GridTextFilter.Matches(r.MessagesUnacknowledged.ToString(CultureInfo.InvariantCulture), q);

    private static bool IsUrlOrSubdomain(string kind) =>
        string.Equals(kind, "Url", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "Subdomain", StringComparison.OrdinalIgnoreCase);

    private static string? ToLiveHref(HttpRequestQueueRowDto row)
    {
        var raw = row.FinalUrl ?? row.RequestUrl;
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        return null;
    }

    private static string? ToLiveHref(AssetGridRowDto row)
    {
        var raw = row.RawValue?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || !IsUrlOrSubdomain(row.Kind))
            return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

        if (string.Equals(row.Kind, "Subdomain", StringComparison.OrdinalIgnoreCase))
        {
            var host = raw.Trim().TrimEnd('/');
            if (host.Length == 0)
                return null;
            return $"https://{host}";
        }

        return null;
    }
    private int OpsTargetActiveFilterCount =>
        CountActive(_targetRootFilter, _targetDepthFilter, _targetGroupBy);

    private int AssetActiveFilterCount =>
        CountActive(_filterKindCol, _filterStatusCol, _filterRawCol, _filterPipelineCol, _filterHowFoundCol, _assetGroupBy)
        + (!_showAssetDepth ? 1 : 0)
        + (_showAssetCanonical ? 1 : 0)
        + (!_showAssetPipeline ? 1 : 0)
        + (!_showAssetHowFound ? 1 : 0);

    private int QueueActiveFilterCount =>
        CountActive(_filterQueueKindCol, _filterQueueRawCol, _filterQueuePipelineCol, _queueGroupBy)
        + (_showFailedQueueRequests ? 1 : 0)
        + (!_showQueueDomain ? 1 : 0)
        + (!_showQueueAttempts ? 1 : 0)
        + (_showQueueTiming ? 1 : 0)
        + (!_showQueueErrors ? 1 : 0);

    private static int CountActive(params string?[] values) =>
        values.Count(value => !string.IsNullOrWhiteSpace(value));


}
