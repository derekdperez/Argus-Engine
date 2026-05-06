using System.Text.Json;

namespace ArgusEngine.Infrastructure.Workers;

public static class SubdomainEnumerationParsers
{
    public static IReadOnlyList<string> ParseSubfinderOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var results = new List<string>();
        using var reader = new StringReader(output);

        while (reader.ReadLine() is { } line)
        {
            if (TryParseSubfinderLine(line, out var candidate))
                results.Add(candidate);
        }

        return results;
    }

    public static bool TryParseSubfinderLine(string line, out string candidate)
    {
        candidate = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.AsSpan().Trim();

        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '{' && trimmed[^1] == '}')
        {
            try
            {
                using var doc = JsonDocument.Parse(line);

                if (TryGetStringProperty(doc.RootElement, "host", out candidate)
                    || TryGetStringProperty(doc.RootElement, "input", out candidate))
                {
                    candidate = candidate.Trim();
                    return candidate.Length > 0;
                }
            }
            catch (JsonException)
            {
                // Fallback to text parsing below.
            }
        }

        candidate = trimmed.ToString();
        return candidate.Length > 0;
    }

    public static IReadOnlyList<string> ParseAmassOutputFile(string outputFilePath)
    {
        if (!File.Exists(outputFilePath))
            return [];

        var results = new List<string>();

        foreach (var line in File.ReadLines(outputFilePath))
        {
            var trimmed = line.AsSpan().Trim();

            if (trimmed.Length > 0)
                results.Add(trimmed.ToString());
        }

        return results;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }
}
