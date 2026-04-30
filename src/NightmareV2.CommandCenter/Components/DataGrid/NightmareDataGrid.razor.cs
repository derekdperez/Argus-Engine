using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace NightmareV2.CommandCenter.Components.DataGrid;

/// <summary>
/// Shared data grid: wraps <see cref="QuickGrid{TGridItem}"/> with toolbar search, optional client paging,
/// virtualization, scroll presets, optional row grouping (<see cref="GroupKeySelector"/>), in-grid row
/// filtering (<see cref="RowMatches"/>), page-size controls, and a configurable toolbar area.
/// </summary>
[CascadingTypeParameter(nameof(TGridItem))]
public partial class NightmareDataGrid<TGridItem>
{
    private IReadOnlyList<IGrouping<string, TGridItem>>? _groups;
    private int _totalRowCount;
    private int _visibleRowCount;

    [Parameter] public IQueryable<TGridItem>? Items { get; set; }

    /// <summary>Optional row filter when <see cref="SearchText"/> is non-empty. Materializes the query to memory.</summary>
    [Parameter] public Func<TGridItem, string, bool>? RowMatches { get; set; }

    /// <summary>When set, rows are split into collapsible groups (materializes filtered items).</summary>
    [Parameter] public Func<TGridItem, string>? GroupKeySelector { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public RenderFragment? ToolbarTemplate { get; set; }

    /// <summary>Advanced grid controls such as column pickers, grouping, density, and column-specific filters.</summary>
    [Parameter] public RenderFragment? ConfigurationTemplate { get; set; }

    [Parameter] public string SearchText { get; set; } = "";

    [Parameter] public EventCallback<string> SearchTextChanged { get; set; }

    [Parameter] public string SearchPlaceholder { get; set; } = "Search…";

    [Parameter] public bool ShowSearch { get; set; } = true;

    /// <summary>When null, toolbar is shown if search, pagination, config, or <see cref="ToolbarTemplate"/> is used.</summary>
    [Parameter] public bool? ShowToolbar { get; set; }

    [Parameter] public PaginationState? Pagination { get; set; }

    [Parameter] public bool ShowPageSizePicker { get; set; } = true;

    [Parameter] public IReadOnlyList<int> PageSizeOptions { get; set; } = [25, 50, 100, 250];

    [Parameter] public bool Virtualize { get; set; }

    [Parameter] public int ItemSize { get; set; } = 40;

    [Parameter] public Func<TGridItem, object?>? ItemKey { get; set; }

    [Parameter] public int OverscanCount { get; set; } = 5;

    [Parameter] public string Theme { get; set; } = "default";

    [Parameter] public string GridTableClass { get; set; } = "nightmare-qg";

    [Parameter] public NightmareDataGridScrollPreset ScrollPreset { get; set; } = NightmareDataGridScrollPreset.Compact;

    [Parameter] public string? HostStyle { get; set; }

    [Parameter] public string CssClass { get; set; } = "";

    [Parameter] public int? HostTabIndex { get; set; }

    [Parameter] public bool IsLoading { get; set; }

    [Parameter] public string EmptyText { get; set; } = "No rows to display.";

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>When true, shows the number of rows after search / <see cref="RowMatches"/> filtering.</summary>
    [Parameter] public bool ShowRowCount { get; set; } = true;

    private bool ToolbarVisible =>
        ShowToolbar ?? (ShowSearch || Pagination is not null || ConfigurationTemplate is not null || ToolbarTemplate is not null);

    private bool HasConfiguration => ConfigurationTemplate is not null;

    private bool EffectiveVirtualize => Virtualize && GroupKeySelector is null;

    private bool CanPage => Pagination is not null && !EffectiveVirtualize && GroupKeySelector is null;

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
            return scroll;
        }
    }

    protected override void OnParametersSet()
    {
        var baseItems = Items ?? Enumerable.Empty<TGridItem>().AsQueryable();
        _totalRowCount = baseItems.Count();

        if (GroupKeySelector is null)
        {
            _groups = null;
            _visibleRowCount = GetEffectiveItems().Count();
            return;
        }

        var list = GetEffectiveItems().ToList();
        _visibleRowCount = list.Count;
        _groups = list
            .GroupBy(row => GroupKeySelector(row) ?? "Unassigned", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IQueryable<TGridItem> GetEffectiveItems()
    {
        var q = Items ?? Enumerable.Empty<TGridItem>().AsQueryable();
        if (RowMatches is not null && !string.IsNullOrWhiteSpace(SearchText))
            return q.AsEnumerable().Where(x => RowMatches(x, SearchText)).AsQueryable();
        return q;
    }

    private PaginationState? EffectivePagination => CanPage ? Pagination : null;

    private async Task OnSearchInput(ChangeEventArgs e)
    {
        SearchText = e.Value?.ToString() ?? "";
        if (Pagination is not null)
            await Pagination.SetCurrentPageIndexAsync(0).ConfigureAwait(false);
        await SearchTextChanged.InvokeAsync(SearchText).ConfigureAwait(false);
    }

    private async Task OnPageSizeChanged(ChangeEventArgs e)
    {
        if (Pagination is null)
            return;

        if (int.TryParse(e.Value?.ToString(), out var pageSize) && pageSize > 0)
        {
            Pagination.ItemsPerPage = pageSize;
            await Pagination.SetCurrentPageIndexAsync(0).ConfigureAwait(false);
        }
    }
}
