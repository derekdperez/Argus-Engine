namespace NightmareV2.CommandCenter.Components.DataGrid;

/// <summary>Scroll host height presets for sticky-header grids.</summary>
public enum NightmareDataGridScrollPreset
{
    /// <summary>No max-height (table grows with content).</summary>
    None,

    Compact,
    Medium,
    Tall,

    /// <summary>Tall scroll region with fixed row height for virtualized rows.</summary>
    Virtualized,
}
