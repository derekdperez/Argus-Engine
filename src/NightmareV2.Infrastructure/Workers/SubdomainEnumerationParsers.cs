using System.Text.Json;

namespace NightmareV2.Infrastructure.Workers;

public static class SubdomainEnumerationParsers
{
    public static IReadOnlyList<string> ParseSubfinderOutput(string output)
    {
        var results = new List<string>();
        using var reader = new StringReader(output ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            string? candidate = null;
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty("host", out var hostProp) && hostProp.ValueKind == JsonValueKind.String)
                        candidate = hostProp.GetString();
                    else if (doc.RootElement.TryGetProperty("input", out var inputProp) && inputProp.ValueKind == JsonValueKind.String)
                        candidate = inputProp.GetString();
                }
                catch
                {
                    // Fallback to text parsing below.
                }
            }

            candidate ??= trimmed;
            if (!string.IsNullOrWhiteSpace(candidate))
                results.Add(candidate.Trim());
        }

        return results;
    }

    public static IReadOnlyList<string> ParseAmassOutputFile(string outputFilePath)
    {
        if (!File.Exists(outputFilePath))
            return [];

        return File.ReadLines(outputFilePath)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }
}
