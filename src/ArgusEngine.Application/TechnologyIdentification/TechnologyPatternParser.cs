namespace ArgusEngine.Application.TechnologyIdentification;

public static class TechnologyPatternParser
{
    public static ParsedTechnologyPattern Parse(string? raw)
    {
        var parts = SplitOnBackslashSemicolon(raw ?? "");
        var pattern = parts.Count == 0 ? ".*" : parts[0];
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = ".*";

        var confidence = 100;
        string? version = null;

        foreach (var tag in parts.Skip(1))
        {
            var i = tag.IndexOf(':', StringComparison.Ordinal);
            if (i <= 0)
                continue;

            var key = tag[..i].Trim().ToLowerInvariant();
            var value = tag[(i + 1)..].Trim();
            if (key == "confidence" && int.TryParse(value, out var parsed))
                confidence = Math.Clamp(parsed, 0, 100);
            else if (key == "version")
                version = value;
        }

        return new ParsedTechnologyPattern(pattern, confidence, version);
    }

    private static List<string> SplitOnBackslashSemicolon(string value)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length - 1; i++)
        {
            if (value[i] != '\\' || value[i + 1] != ';')
                continue;

            parts.Add(value[start..i]);
            start = i + 2;
            i++;
        }

        parts.Add(value[start..]);
        return parts;
    }
}
