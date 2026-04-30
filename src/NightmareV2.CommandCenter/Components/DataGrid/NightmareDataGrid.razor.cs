using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;
using Microsoft.JSInterop;

namespace NightmareV2.CommandCenter.Components.DataGrid;

/// <summary>
/// Shared data grid wrapper around <see cref="QuickGrid{TGridItem}"/>.
/// It adds client-side search, debouncing, cached filtering, grouping, density controls,
/// persistent grid preferences, CSV export, refresh hooks, and common empty/loading states.
/// </summary>
[CascadingTypeParameter(nameof(TGridItem))]
public partial class NightmareDataGrid<TGridItem> : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PaginationState _fallbackPagination = new();

    private IReadOnlyList<TGridItem> _effectiveRows = [];
    private IReadOnlyList<GridGroup<TGridItem>>? _groups;
    private int _totalRowCount;
    private int _visibleRowCount;
    private string _searchInputText = "";
    private CancellationTokenSource? _searchDebounceCts;
    private bool _preferencesLoaded;
    private bool _suppressPreferenceWrite;

    [Inject] private IJSRuntime Js { get; set; } = default!;

    [Parameter] public IQueryable<TGridItem>? Items { get; set; }

    /// <summary>Optional row filter when <see cref="SearchText"/> is non-empty. The current page data is materialized once.</summary>
    [Parameter] public Func<TGridItem, string, bool>? RowMatches { get; set; }

    /// <summary>When set, rows are split into collapsible groups. Grouped views are intentionally not virtualized.</summary>
    [Parameter] public Func<TGridItem, string>? GroupKeySelector { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public RenderFragment? ToolbarTemplate { get; set; }

    /// <summary>Advanced grid controls such as column pickers, grouping, density, and column-specific filters.</summary>
    [Parameter] public RenderFragment? ConfigurationTemplate { get; set; }

    [Parameter] public RenderFragment? EmptyTemplate { get; set; }

    [Parameter] public RenderFragment? LoadingTemplate { get; set; }

    [Parameter] public string SearchText { get; set; } = "";

    [Parameter] public EventCallback<string> SearchTextChanged { get; set; }

    [Parameter] public int SearchDebounceMilliseconds { get; set; } = 180;

    [Parameter] public string SearchPlaceholder { get; set; } = "Search…";

    [Parameter] public bool ShowSearch { get; set; } = true;

    /// <summary>When null, toolbar is shown if search, pagination, config, refresh, export, or <see cref="ToolbarTemplate"/> is used.</summary>
    [Parameter] public bool? ShowToolbar { get; set; }

    [Parameter] public PaginationState? Pagination { get; set; }

    [Parameter] public bool ShowPageSizePicker { get; set; } = true;

    [Parameter] public IReadOnlyList<int> PageSizeOptions { get; set; } = [25, 50, 100, 250];

    [Parameter] public bool Virtualize { get; set; }

    [Parameter] public int ItemSize { get; set; } = 0;

    [Parameter] public Func<TGridItem, object?>? ItemKey { get; set; }

    [Parameter] public int OverscanCount { get; set; } = 5;

    [Parameter] public string Theme { get; set; } = "default";

    [Parameter] public string GridTableClass { get; set; } = "nightmare-qg";

    [Parameter] public NightmareDataGridScrollPreset ScrollPreset { get; set; } = NightmareDataGridScrollPreset.Compact;

    [Parameter] public NightmareDataGridDensity Density { get; set; } = NightmareDataGridDensity.Compact;

    [Parameter] public EventCallback<NightmareDataGridDensity> DensityChanged { get; set; }

    [Parameter] public bool ShowDensityPicker { get; set; }

    [Parameter] public string? HostStyle { get; set; }

    [Parameter] public int? MaxHeightPixels { get; set; }

    [Parameter] public int? VirtualizedHeightPixels { get; set; }

    [Parameter] public string CssClass { get; set; } = "";

    [Parameter] public int? HostTabIndex { get; set; }

    [Parameter] public bool IsLoading { get; set; }

    [Parameter] public string LoadingText { get; set; } = "Loading rows…";

    [Parameter] public string EmptyText { get; set; } = "No rows to display.";

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>When true, shows the number of rows after search and column filtering.</summary>
    [Parameter] public bool ShowRowCount { get; set; } = true;

    [Parameter] public EventCallback RefreshRequested { get; set; }

    [Parameter] public string RefreshButtonText { get; set; } = "Refresh";

    [Parameter] public DateTimeOffset? LastUpdatedUtc { get; set; }

    [Parameter] public EventCallback ClearFiltersRequested { get; set; }

    [Parameter] public int ActiveFilterCount { get; set; }

    [Parameter] public bool ShowClearFiltersButton { get; set; } = true;

    [Parameter] public bool EnableCsvExport { get; set; }

    [Parameter] public string ExportFileName { get; set; } = "grid-export.csv";

    [Parameter] public int MaxExportRows { get; set; } = 5000;

    [Parameter] public bool ShowBottomPager { get; set; } = true;

    [Parameter] public bool GroupsInitiallyExpanded { get; set; } = true;

    /// <summary>Optional key used to persist search, density, and page size in localStorage.</summary>
    [Parameter] public string? PersistKey { get; set; }

    [Parameter] public bool PersistSearch { get; set; } = true;

    [Parameter] public bool PersistPageSize { get; set; } = true;

    [Parameter] public bool PersistDensity { get; set; } = true;

    private IQueryable<TGridItem> EffectiveItems => _effectiveRows.AsQueryable();

    private IEnumerable<int> NormalizedPageSizeOptions =>
        PageSizeOptions
            .Append(Pagination?.ItemsPerPage ?? 0)
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x);

    private bool ToolbarVisible =>
        ShowToolbar ?? (ShowSearch
            || Pagination is not null
            || ConfigurationTemplate is not null
            || ToolbarTemplate is not null
            || RefreshRequested.HasDelegate
            || EnableCsvExport
            || ShowDensityPicker);

    private bool HasConfiguration => ConfigurationTemplate is not null;

    private bool EffectiveVirtualize => Virtualize && GroupKeySelector is null;

    private bool CanPage => Pagination is not null && !EffectiveVirtualize && GroupKeySelector is null;

    private PaginationState? EffectivePagination => CanPage ? Pagination : null;

    private PaginationState ActivePagination => Pagination ?? _fallbackPagination;

    private int EffectiveItemSize =>
        Density switch
        {
            NightmareDataGridDensity.Relaxed => Math.Max(ItemSize, 52),
            NightmareDataGridDensity.Comfortable => Math.Max(ItemSize, 44),
            _ => ItemSize > 0 ? ItemSize : 38,
        };

    private string DensityCssClass => Density switch
    {
        NightmareDataGridDensity.Relaxed => "density-relaxed",
        NightmareDataGridDensity.Comfortable => "density-comfortable",
        _ => "density-compact",
    };

    private int EffectiveActiveFilterCount => ActiveFilterCount + (string.IsNullOrWhiteSpace(SearchText) ? 0 : 1);

    private bool HasAnyActiveFilter => EffectiveActiveFilterCount > 0;

    private string HostCssClasses
    {
        get
        {
            var scroll = ScrollPreset switch
            {
                NightmareDataGridScrollPreset.Compact => "nightmare-dg-host short",
                NightmareDataGridScrollPreset.Medium => "nightmare-dg-host mid",
                NightmareDataGridScrollPreset.Tall => "nightmare-dg-host tall",
                NightmareDataGridScrollPreset.Virtualized => "nightmare-dg-host vq",
                _ => "nightmare-dg-host",
            };

            if (EffectiveVirtualize && ScrollPreset != NightmareDataGridScrollPreset.Virtualized)
                scroll += " vq";

            if (StickyFirstColumn)
                scroll += " sticky-first";

            return scroll;
        }
    }

    [Parameter] public bool StickyFirstColumn { get; set; }

    private string EffectiveHostStyle
    {
        get
        {
            var declarations = new List<string>
            {
                $"--dg-item-size: {EffectiveItemSize.ToString(CultureInfo.InvariantCulture)}px",
            };

            if (MaxHeightPixels is > 0)
                declarations.Add($"--dg-max-height: {MaxHeightPixels.Value.ToString(CultureInfo.InvariantCulture)}px");

            if (VirtualizedHeightPixels is > 0)
                declarations.Add($"--dg-virtualized-height: {VirtualizedHeightPixels.Value.ToString(CultureInfo.InvariantCulture)}px");

            if (!string.IsNullOrWhiteSpace(HostStyle))
                declarations.Add(HostStyle!.Trim().TrimEnd(';'));

            return string.Join("; ", declarations);
        }
    }

    protected override void OnParametersSet()
    {
        if (_searchInputText != SearchText && _searchDebounceCts is null)
            _searchInputText = SearchText;

        RebuildRows();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            await LoadPreferencesAsync();
    }

    private void RebuildRows()
    {
        var source = Items ?? Enumerable.Empty<TGridItem>().AsQueryable();
        var baseRows = source.ToList();
        _totalRowCount = baseRows.Count;

        if (RowMatches is not null && !string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            _effectiveRows = baseRows
                .Where(row => RowMatches(row, search))
                .ToList();
        }
        else
        {
            _effectiveRows = baseRows;
        }

        _visibleRowCount = _effectiveRows.Count;

        if (GroupKeySelector is null)
        {
            _groups = null;
            return;
        }

        _groups = _effectiveRows
            .GroupBy(row => NormalizeGroupKey(GroupKeySelector(row)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GridGroup<TGridItem>(group.Key, group.ToList()))
            .ToList();
    }

    private static string NormalizeGroupKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "Unassigned" : key.Trim();

    private Task OnSearchInput(ChangeEventArgs e)
    {
        _searchInputText = e.Value?.ToString() ?? "";
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = null;

        if (SearchDebounceMilliseconds <= 0)
            return CommitSearchAsync(_searchInputText);

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebounceSearchAsync(_searchInputText, cts);

        return Task.CompletedTask;
    }

    private async Task DebounceSearchAsync(string value, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, cts.Token);
            await InvokeAsync(async () =>
            {
                if (!cts.IsCancellationRequested)
                    await CommitSearchAsync(value);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(_searchDebounceCts, cts))
                _searchDebounceCts = null;

            cts.Dispose();
        }
    }

    private async Task CommitSearchAsync(string value)
    {
        value ??= "";
        if (SearchText == value)
            return;

        SearchText = value;
        if (Pagination is not null)
            await Pagination.SetCurrentPageIndexAsync(0);

        await SearchTextChanged.InvokeAsync(SearchText);
        await SavePreferencesAsync();
    }

    private async Task OnPageSizeChanged(ChangeEventArgs e)
    {
        if (Pagination is null)
            return;

        if (int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pageSize) && pageSize > 0)
        {
            Pagination.ItemsPerPage = pageSize;
            await Pagination.SetCurrentPageIndexAsync(0);
            await SavePreferencesAsync();
        }
    }

    private async Task OnDensityChanged(ChangeEventArgs e)
    {
        if (!Enum.TryParse<NightmareDataGridDensity>(e.Value?.ToString(), ignoreCase: true, out var density))
            return;

        Density = density;
        await DensityChanged.InvokeAsync(density);
        await SavePreferencesAsync();
    }

    private Task InvokeRefreshAsync() => RefreshRequested.InvokeAsync();


    private async Task ClearAllFiltersAsync()
    {
        _searchInputText = "";
        await CommitSearchAsync("");

        if (ClearFiltersRequested.HasDelegate)
            await ClearFiltersRequested.InvokeAsync();

        if (Pagination is not null)
            await Pagination.SetCurrentPageIndexAsync(0);

        await SavePreferencesAsync();
    }

    private async Task ExportCsvAsync()
    {
        if (!EnableCsvExport || _visibleRowCount == 0)
            return;

        var rows = _effectiveRows.Take(Math.Max(1, MaxExportRows)).ToList();
        var csv = BuildCsv(rows);
        var fileName = string.IsNullOrWhiteSpace(ExportFileName) ? "grid-export.csv" : ExportFileName;
        await Js.InvokeVoidAsync("nightmareUi.downloadTextFile", fileName, csv, "text/csv;charset=utf-8");
    }

    private static string BuildCsv(IReadOnlyList<TGridItem> rows)
    {
        var props = typeof(TGridItem)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && IsExportableType(p.PropertyType))
            .ToArray();

        var sb = new StringBuilder(capacity: Math.Max(256, rows.Count * Math.Max(32, props.Length * 16)));
        sb.Append('\uFEFF');

        if (props.Length == 0)
        {
            sb.AppendLine("Value");
            foreach (var row in rows)
                AppendCsvRow(sb, new[] { row?.ToString() ?? string.Empty });
            return sb.ToString();
        }

        AppendCsvRow(sb, props.Select(p => p.Name));

        foreach (var row in rows)
        {
            AppendCsvRow(sb, props.Select(prop =>
            {
                try
                {
                    return FormatCsvValue(prop.GetValue(row));
                }
                catch
                {
                    return string.Empty;
                }
            }));
        }

        return sb.ToString();
    }

    private static bool IsExportableType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(Guid)
            || type == typeof(Uri);
    }

    private static string FormatCsvValue(object? value) =>
        value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };

    private static void AppendCsvRow(StringBuilder sb, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
                sb.Append(',');
            first = false;

            sb.Append(EscapeCsv(value));
        }

        sb.AppendLine();
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { '"', ',', '\r', '\n' }) < 0)
            return value;

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private async Task LoadPreferencesAsync()
    {
        if (string.IsNullOrWhiteSpace(PersistKey) || _preferencesLoaded)
            return;

        _preferencesLoaded = true;

        try
        {
            var json = await Js.InvokeAsync<string?>("nightmareUi.getLocalStorage", StorageKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var prefs = JsonSerializer.Deserialize<GridPreferences>(json, JsonOptions);
            if (prefs is null)
                return;

            _suppressPreferenceWrite = true;

            if (PersistPageSize && Pagination is not null && prefs.PageSize is > 0)
                Pagination.ItemsPerPage = prefs.PageSize.Value;

            if (PersistDensity && !string.IsNullOrWhiteSpace(prefs.Density)
                && Enum.TryParse<NightmareDataGridDensity>(prefs.Density, ignoreCase: true, out var density))
            {
                Density = density;
                await DensityChanged.InvokeAsync(density);
            }

            if (PersistSearch && prefs.SearchText is not null)
            {
                _searchInputText = prefs.SearchText;
                SearchText = prefs.SearchText;
                await SearchTextChanged.InvokeAsync(SearchText);
            }
        }
        catch (InvalidOperationException)
        {
            // JS interop is unavailable during prerender. The grid still works without persisted preferences.
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }
        catch (JsonException)
        {
        }
        finally
        {
            _suppressPreferenceWrite = false;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task SavePreferencesAsync()
    {
        if (_suppressPreferenceWrite || string.IsNullOrWhiteSpace(PersistKey))
            return;

        try
        {
            var prefs = new GridPreferences(
                PersistSearch ? SearchText : null,
                PersistPageSize ? Pagination?.ItemsPerPage : null,
                PersistDensity ? Density.ToString() : null);

            var json = JsonSerializer.Serialize(prefs, JsonOptions);
            await Js.InvokeVoidAsync("nightmareUi.setLocalStorage", StorageKey, json);
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }
    }

    private string StorageKey => $"nightmare:v2:grid:{PersistKey}";

    public async ValueTask DisposeAsync()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        await ValueTask.CompletedTask;
    }

    private sealed record GridGroup<TRow>(string Key, IReadOnlyList<TRow> Items)
    {
        public IQueryable<TRow> Query => Items.AsQueryable();
    }

    private sealed record GridPreferences(string? SearchText, int? PageSize, string? Density);
}
