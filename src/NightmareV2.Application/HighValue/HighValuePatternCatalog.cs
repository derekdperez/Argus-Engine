using System.Text.Json;

namespace NightmareV2.Application.HighValue;

/// <summary>Loads <c>high_value_targets.txt</c> (JSON array) from disk.</summary>
public static class HighValuePatternCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<RegexRuleDefinition> LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<RegexRuleDefinition>();

        var text = File.ReadAllText(path);
        var rows = JsonSerializer.Deserialize<List<PatternJsonRow>>(text, Json) ?? [];
        var list = new List<RegexRuleDefinition>(rows.Count);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Scope) || string.IsNullOrWhiteSpace(r.Regex))
                continue;
            list.Add(
                new RegexRuleDefinition(
                    r.Name.Trim(),
                    r.Scope.Trim().ToLowerInvariant(),
                    r.Regex,
                    r.Description ?? "",
                    r.OutputFilename ?? "",
                    r.ImportanceScore));
        }

        return list;
    }

    private sealed class PatternJsonRow
    {
        public string? Name { get; set; }
        public string? Scope { get; set; }
        public string? Regex { get; set; }
        public string? Description { get; set; }
        public string? OutputFilename { get; set; }
        public int ImportanceScore { get; set; }
    }
}
