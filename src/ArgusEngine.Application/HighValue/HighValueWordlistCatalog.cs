namespace ArgusEngine.Application.HighValue;

/// <summary>Loads all <c>.txt</c> wordlists from a directory (one category per file stem).</summary>
public static class HighValueWordlistCatalog
{
    public static IReadOnlyList<(string Category, IReadOnlyList<string> Lines)> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<(string, IReadOnlyList<string>)>();

        var files = Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var list = new List<(string, IReadOnlyList<string>)>(files.Length);
        foreach (var path in files)
        {
            var cat = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(cat))
                continue;
            var lines = new List<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                lines.Add(line);
            }

            if (lines.Count > 0)
                list.Add((cat, lines));
        }

        return list;
    }
}
