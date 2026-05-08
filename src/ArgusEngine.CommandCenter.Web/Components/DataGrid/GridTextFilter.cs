namespace ArgusEngine.CommandCenter.Web.Components.DataGrid;

public static class GridTextFilter
{
    public static bool Matches(string? value, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var tokens = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        foreach (var token in tokens)
        {
            if (!value.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

