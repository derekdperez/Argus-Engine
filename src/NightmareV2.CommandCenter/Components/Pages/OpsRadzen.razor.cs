using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Radzen;
using System.Linq;
using Microsoft.AspNetCore.Components.QuickGrid;
using NightmareV2.CommandCenter.Components.DataGrid;
using NightmareV2.CommandCenter.Models;

namespace NightmareV2.CommandCenter.Components.Pages;

public partial class OpsRadzen
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


    private static readonly int[] _gridPageSizeOptions = new[] { 25, 50, 100, 250, 500 };
    private static readonly int[] _sideGridPageSizeOptions = new[] { 10, 25, 50, 100 };

    private int _targetPageSize = 25;
    private int _largeGridPageSize = 50;
    private int _sideGridPageSize = 10;
    private string _densityName = "Compact";
    private Density _radzenDensity = Density.Compact;
    private const string RadzenGridPreferencesStorageKey = "nightmare.ops.radzen.grid.preferences.v1";
    private bool _radzenGridPreferencesLoaded;


    private string SelectedDensity
    {
        get => _densityName;
        set
        {
            _densityName = value;
            _radzenDensity = string.Equals(value, "Compact", StringComparison.OrdinalIgnoreCase)
                ? Density.Compact
                : Density.Default;
        }
    }

    private IReadOnlyList<TargetSummary> RadzenTargets =>
        FilteredOpsTargets
            .Where(t => TargetRowMatches(t, _targetSearch))
            .ToList();

    private IReadOnlyList<AssetGridRowDto> RadzenAssets =>
        FilteredAssets.ToList();

    private IReadOnlyList<HttpRequestQueueRowDto> RadzenRequestQueue =>
        FilteredRequestQueue.ToList();

    private IReadOnlyList<WorkerKindSummaryDto> RadzenWorkers =>
        WorkerRows
            .Where(w => WorkerRowMatches(w, _workerSearch))
            .ToList();

    private IReadOnlyList<RabbitQueueBriefDto> RadzenRabbitQueues =>
        RabbitQueueRows
            .Where(q => RabbitRowMatches(q, _rabbitSearch))
            .ToList();


    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _radzenGridPreferencesLoaded)
            return;

        _radzenGridPreferencesLoaded = true;
        await LoadRadzenGridPreferencesAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadRadzenGridPreferencesAsync()
    {
        try
        {
            var json = await Js.InvokeAsync<string?>("nightmareUi.getLocalStorage", RadzenGridPreferencesStorageKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var prefs = JsonSerializer.Deserialize<RadzenOpsGridPreferences>(json);
            if (prefs is null)
                return;

            SelectedDensity = prefs.Density ?? SelectedDensity;
            _targetPageSize = CoercePageSize(prefs.TargetPageSize, _gridPageSizeOptions, _targetPageSize);
            _largeGridPageSize = CoercePageSize(prefs.LargeGridPageSize, _gridPageSizeOptions, _largeGridPageSize);
            _sideGridPageSize = CoercePageSize(prefs.SideGridPageSize, _sideGridPageSizeOptions, _sideGridPageSize);

            _targetVirtualize = prefs.TargetVirtualize ?? _targetVirtualize;
            _assetVirtualize = prefs.AssetVirtualize ?? _assetVirtualize;
            _queueVirtualize = prefs.QueueVirtualize ?? _queueVirtualize;

            _showAssetDepth = prefs.ShowAssetDepth ?? _showAssetDepth;
            _showAssetCanonical = prefs.ShowAssetCanonical ?? _showAssetCanonical;
            _showAssetPipeline = prefs.ShowAssetPipeline ?? _showAssetPipeline;
            _showAssetHowFound = prefs.ShowAssetHowFound ?? _showAssetHowFound;

            _showQueueDomain = prefs.ShowQueueDomain ?? _showQueueDomain;
            _showQueueAttempts = prefs.ShowQueueAttempts ?? _showQueueAttempts;
            _showQueueTiming = prefs.ShowQueueTiming ?? _showQueueTiming;
            _showQueueErrors = prefs.ShowQueueErrors ?? _showQueueErrors;
        }
        catch
        {
            // Ignore unavailable/corrupt browser storage. The page still works with defaults.
        }
    }

    private async Task SaveRadzenGridPreferencesAsync()
    {
        var prefs = new RadzenOpsGridPreferences
        {
            Density = SelectedDensity,
            TargetPageSize = _targetPageSize,
            LargeGridPageSize = _largeGridPageSize,
            SideGridPageSize = _sideGridPageSize,
            TargetVirtualize = _targetVirtualize,
            AssetVirtualize = _assetVirtualize,
            QueueVirtualize = _queueVirtualize,
            ShowAssetDepth = _showAssetDepth,
            ShowAssetCanonical = _showAssetCanonical,
            ShowAssetPipeline = _showAssetPipeline,
            ShowAssetHowFound = _showAssetHowFound,
            ShowQueueDomain = _showQueueDomain,
            ShowQueueAttempts = _showQueueAttempts,
            ShowQueueTiming = _showQueueTiming,
            ShowQueueErrors = _showQueueErrors,
        };

        var json = JsonSerializer.Serialize(prefs);
        await Js.InvokeVoidAsync("nightmareUi.setLocalStorage", RadzenGridPreferencesStorageKey, json);
        _statusMessage = "Saved Radzen grid layout preferences in this browser.";
    }

    private static int CoercePageSize(int? value, IReadOnlyCollection<int> allowed, int fallback) =>
        value is { } size && allowed.Contains(size) ? size : fallback;

    private Task ExportTargetsCsvAsync() =>
        ExportCsvAsync(
            "nightmare-ops-radzen-targets.csv",
            new[] { "Id", "RootDomain", "GlobalMaxDepth", "CreatedAtUtc" },
            RadzenTargets.Select(t => new object?[] { t.Id, t.RootDomain, t.GlobalMaxDepth, t.CreatedAtUtc }));

    private Task ExportAssetsCsvAsync() =>
        ExportCsvAsync(
            "nightmare-ops-radzen-assets.csv",
            new[] { "Id", "TargetId", "Kind", "LifecycleStatus", "Depth", "RawValue", "CanonicalKey", "DiscoveredBy", "DiscoveryContext", "DiscoveredAtUtc" },
            RadzenAssets.Select(a => new object?[] { a.Id, a.TargetId, a.Kind, a.LifecycleStatus, a.Depth, a.RawValue, a.CanonicalKey, a.DiscoveredBy, a.DiscoveryContext, a.DiscoveredAtUtc }));

    private Task ExportQueueCsvAsync() =>
        ExportCsvAsync(
            "nightmare-ops-radzen-http-queue.csv",
            new[] { "Id", "AssetKind", "State", "RequestUrl", "DomainKey", "AttemptCount", "Priority", "LastHttpStatus", "DurationMs", "CreatedAtUtc", "UpdatedAtUtc", "LastError" },
            RadzenRequestQueue.Select(q => new object?[] { q.Id, q.AssetKind, q.State, q.RequestUrl, q.DomainKey, q.AttemptCount, q.Priority, q.LastHttpStatus, q.DurationMs, q.CreatedAtUtc, q.UpdatedAtUtc, q.LastError }));

    private Task ExportWorkersCsvAsync() =>
        ExportCsvAsync(
            "nightmare-ops-radzen-workers.csv",
            new[] { "WorkerKey", "ToggleEnabled", "InstanceCount", "LastActivityUtc", "RollupActivityLabel" },
            RadzenWorkers.Select(w => new object?[] { w.WorkerKey, w.ToggleEnabled, w.InstanceCount, w.LastActivityUtc, w.RollupActivityLabel }));

    private Task ExportRabbitCsvAsync() =>
        ExportCsvAsync(
            "nightmare-ops-radzen-rabbit-queues.csv",
            new[] { "Name", "MessagesReady", "MessagesUnacknowledged", "Consumers", "LikelyWorkerKey" },
            RadzenRabbitQueues.Select(q => new object?[] { q.Name, q.MessagesReady, q.MessagesUnacknowledged, q.Consumers, q.LikelyWorkerKey }));

    private async Task ExportCsvAsync(string fileName, IEnumerable<string> headers, IEnumerable<object?[]> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in rows)
            csv.AppendLine(string.Join(",", row.Select(EscapeCsv)));

        await Js.InvokeVoidAsync("nightmareUi.downloadTextFile", fileName, csv.ToString(), "text/csv;charset=utf-8");
    }

    private static string EscapeCsv(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTimeOffset dto => dto.ToString("u", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }



    private sealed class RadzenOpsGridPreferences
    {
        public string? Density { get; set; }
        public int? TargetPageSize { get; set; }
        public int? LargeGridPageSize { get; set; }
        public int? SideGridPageSize { get; set; }
        public bool? TargetVirtualize { get; set; }
        public bool? AssetVirtualize { get; set; }
        public bool? QueueVirtualize { get; set; }
        public bool? ShowAssetDepth { get; set; }
        public bool? ShowAssetCanonical { get; set; }
        public bool? ShowAssetPipeline { get; set; }
        public bool? ShowAssetHowFound { get; set; }
        public bool? ShowQueueDomain { get; set; }
        public bool? ShowQueueAttempts { get; set; }
        public bool? ShowQueueTiming { get; set; }
        public bool? ShowQueueErrors { get; set; }
    }

}
