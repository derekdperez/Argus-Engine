namespace NightmareV2.CommandCenter.Components.DataGrid;

public static class GridTextFilter
{
    public static bool Matches(string? value, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false;
    }
}
