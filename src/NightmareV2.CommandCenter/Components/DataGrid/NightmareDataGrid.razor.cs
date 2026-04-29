using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.QuickGrid;

namespace NightmareV2.CommandCenter.Components.DataGrid;

/// <summary>
/// Shared data grid: wraps <see cref="QuickGrid{TGridItem}"/> with toolbar search, optional client paging,
/// virtualization, scroll presets, optional row grouping (<see cref="GroupKeySelector"/>), and optional
/// in-grid row filter (<see cref="RowMatches"/>), and an optional visible row count (<see cref="ShowRowCount"/>).
/// Sorting and column templates use QuickGrid columns; use
/// <c>GridDateCell</c>, <c>GridCodeCell</c>, <c>GridEllipsisCell</c>, and <c>GridNumberCell</c> for common cell renderings.
/// </summary>
[CascadingTypeParameter(nameof(TGridItem))]
public partial class NightmareDataGrid<TGridItem>
{
    private IReadOnlyList<IGrouping<string, TGridItem>>? _groups;

    [Parameter] public IQueryable<TGridItem>? Items { get; set; }

    /// <summary>Optional row filter when <see cref="SearchText"/> is non-empty. Materializes the query to memory.</summary>
    [Parameter] public Func<TGridItem, string, bool>? RowMatches { get; set; }

    /// <summary>When set, rows are split into collapsible groups (materializes filtered items).</summary>
    [Parameter] public Func<TGridItem, string>? GroupKeySelector { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public RenderFragment? ToolbarTemplate { get; set; }

    [Parameter] public string SearchText { get; set; } = "";

    [Parameter] public EventCallback<string> SearchTextChanged { get; set; }

    [Parameter] public string SearchPlaceholder { get; set; } = "Search…";

    [Parameter] public bool ShowSearch { get; set; } = true;

    /// <summary>When null, toolbar is shown if search, pagination, or <see cref="ToolbarTemplate"/> is used.</summary>
    [Parameter] public bool? ShowToolbar { get; set; }

    [Parameter] public PaginationState? Pagination { get; set; }

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

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>When true, shows the number of rows after search / <see cref="RowMatches"/> filtering (same set passed to QuickGrid).</summary>
    [Parameter] public bool ShowRowCount { get; set; } = true;

    private int _visibleRowCount;

    private bool ToolbarVisible =>
        ShowToolbar ?? (ShowSearch || Pagination is not null || ToolbarTemplate is not null);

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
            if (Virtualize && ScrollPreset != NightmareDataGridScrollPreset.Virtualized)
                scroll += " vq";
            return scroll;
        }
    }

    protected override void OnParametersSet()
    {
        if (GroupKeySelector is null)
        {
            _groups = null;
            _visibleRowCount = GetEffectiveItems().Count();
            return;
        }

        var list = GetEffectiveItems().ToList();
        _visibleRowCount = list.Count;
        _groups = list
            .GroupBy(GroupKeySelector, StringComparer.OrdinalIgnoreCase)
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

    private PaginationState? EffectivePagination => Virtualize ? null : Pagination;

    private Task OnSearchInput(ChangeEventArgs e)
    {
        SearchText = e.Value?.ToString() ?? "";
        return SearchTextChanged.InvokeAsync(SearchText);
    }
}
