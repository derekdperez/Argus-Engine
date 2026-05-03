using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArgusEngine.Application.TechnologyIdentification;

public sealed class TechnologyCatalogLoader(ILogger<TechnologyCatalogLoader>? logger = null)
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private static readonly Action<ILogger, int, int, int, int, Exception?> LogTechnologyCatalogLoaded =
        LoggerMessage.Define<int, int, int, int>(
            LogLevel.Information,
            new EventId(1, nameof(LogTechnologyCatalogLoaded)),
            "Technology catalog loaded: files={FilesLoaded}, technologies={TechnologyCount}, patternsCompiled={PatternsCompiled}, patternsSkipped={PatternsSkipped}");

    private readonly ILogger<TechnologyCatalogLoader> _logger = logger ?? NullLogger<TechnologyCatalogLoader>.Instance;

    public TechnologyCatalog Load(string technologyDetectionRoot)
    {
        if (string.IsNullOrWhiteSpace(technologyDetectionRoot))
            throw new ArgumentException("A technology catalog root is required.", nameof(technologyDetectionRoot));

        var root = new DirectoryInfo(technologyDetectionRoot);
        var technologyDirs = GetTechnologyDirectories(root).ToList();
        if (technologyDirs.Count == 0)
            throw new DirectoryNotFoundException($"Technology catalog directory not found under: {root.FullName}");

        var categories = LoadCategories(root);
        var technologies = new Dictionary<string, TechnologyDefinition>(StringComparer.OrdinalIgnoreCase);
        var filesLoaded = 0;
        var patternsCompiled = 0;
        var patternsSkipped = 0;

        foreach (var file in technologyDirs
            .SelectMany(d => d.EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)))
        {
            if (file.Name.Equals("all.json", StringComparison.OrdinalIgnoreCase)
                || file.Name.Equals("categories.json", StringComparison.OrdinalIgnoreCase)
                || file.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = file.OpenRead();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                continue;

            filesLoaded++;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var definition = BuildDefinition(property.Name, property.Value, ref patternsCompiled, ref patternsSkipped);
                technologies[property.Name] = definition;
            }
        }

        LogTechnologyCatalogLoaded(_logger, filesLoaded, technologies.Count, patternsCompiled, patternsSkipped, null);

        return new TechnologyCatalog(technologies, categories, filesLoaded, patternsCompiled, patternsSkipped);
    }

    private static IEnumerable<DirectoryInfo> GetTechnologyDirectories(DirectoryInfo root)
    {
        var legacyDir = new DirectoryInfo(Path.Combine(root.FullName, "TechIdentificationData"));
        if (legacyDir.Exists)
            yield return legacyDir;

        var technologyDir = new DirectoryInfo(Path.Combine(root.FullName, "technologies"));
        if (technologyDir.Exists)
            yield return technologyDir;
    }

    private static TechnologyDefinition BuildDefinition(
        string name,
        JsonElement element,
        ref int patternsCompiled,
        ref int patternsSkipped)
    {
        var patterns = new List<TechnologyPattern>();

        AddNamedPatternMap(name, element, "headers", TechnologyConstants.HeaderSource, patterns, ref patternsCompiled, ref patternsSkipped);
        AddNamedPatternMap(name, element, "cookies", TechnologyConstants.CookieSource, patterns, ref patternsCompiled, ref patternsSkipped);
        AddNamedPatternMap(name, element, "meta", TechnologyConstants.MetaSource, patterns, ref patternsCompiled, ref patternsSkipped);
        AddPatternValues(name, element, "html", TechnologyConstants.HtmlSource, null, patterns, ref patternsCompiled, ref patternsSkipped);
        AddPatternValues(name, element, "script", TechnologyConstants.ScriptSource, null, patterns, ref patternsCompiled, ref patternsSkipped);
        AddPatternValues(name, element, "url", TechnologyConstants.UrlSource, null, patterns, ref patternsCompiled, ref patternsSkipped);

        var categories = ReadIntList(element, "cats");
        var implies = ReadRelatedRules(element, "implies");
        var requires = ReadStringList(element, "requires");
        var excludes = ReadStringList(element, "excludes");

        var description = ReadString(element, "description");
        var website = ReadString(element, "website");
        var metadata = JsonSerializer.Serialize(new
        {
            categories,
            description,
            website,
            icon = ReadString(element, "icon"),
        }, MetadataJsonOptions);

        return new TechnologyDefinition(
            name,
            description,
            website,
            categories,
            patterns,
            implies,
            requires,
            excludes,
            metadata);
    }

    private static void AddNamedPatternMap(
        string technologyName,
        JsonElement element,
        string propertyName,
        string source,
        List<TechnologyPattern> patterns,
        ref int patternsCompiled,
        ref int patternsSkipped)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            return;

        foreach (var item in property.EnumerateObject())
        {
            AddPatternElement(
                technologyName,
                source,
                item.Name,
                item.Value,
                patterns,
                ref patternsCompiled,
                ref patternsSkipped);
        }
    }

    private static void AddPatternValues(
        string technologyName,
        JsonElement element,
        string propertyName,
        string source,
        string? key,
        List<TechnologyPattern> patterns,
        ref int patternsCompiled,
        ref int patternsSkipped)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return;

        AddPatternElement(technologyName, source, key, property, patterns, ref patternsCompiled, ref patternsSkipped);
    }

    private static void AddPatternElement(
        string technologyName,
        string source,
        string? key,
        JsonElement element,
        List<TechnologyPattern> patterns,
        ref int patternsCompiled,
        ref int patternsSkipped)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddPattern(technologyName, source, key, element.GetString() ?? "", patterns, ref patternsCompiled, ref patternsSkipped);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AddPatternElement(technologyName, source, key, item, patterns, ref patternsCompiled, ref patternsSkipped);
                break;
            case JsonValueKind.Object:
                foreach (var nested in element.EnumerateObject())
                    AddPatternElement(technologyName, source, nested.Name, nested.Value, patterns, ref patternsCompiled, ref patternsSkipped);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                AddPattern(technologyName, source, key, element.GetRawText(), patterns, ref patternsCompiled, ref patternsSkipped);
                break;
        }
    }

    private static void AddPattern(
        string technologyName,
        string source,
        string? key,
        string raw,
        List<TechnologyPattern> patterns,
        ref int patternsCompiled,
        ref int patternsSkipped)
    {
        var parsed = TechnologyPatternParser.Parse(raw);
        try
        {
            var regex = new Regex(
                parsed.RegexPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(250));
            patterns.Add(new TechnologyPattern(
                technologyName,
                source,
                key,
                raw,
                regex,
                parsed.Confidence,
                parsed.VersionExpression));
            patternsCompiled++;
        }
        catch (ArgumentException)
        {
            patternsSkipped++;
        }
    }

    private static Dictionary<int, string> LoadCategories(DirectoryInfo root)
    {
        var categoryFile = new FileInfo(Path.Combine(root.FullName, "categories.json"));
        if (!categoryFile.Exists)
            return new Dictionary<int, string>();

        using var stream = categoryFile.OpenRead();
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<int, string>();

        var categories = new Dictionary<int, string>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var id))
                continue;

            if (property.Value.ValueKind == JsonValueKind.String)
                categories[id] = property.Value.GetString() ?? "";
            else if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("name", out var name))
                categories[id] = name.GetString() ?? "";
        }

        return categories;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static List<int> ReadIntList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return [];

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var single))
            return [single];

        if (property.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value))
                values.Add(value);
        }

        return values;
    }

    private static string[] ReadStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return [];

        return ReadStringLikeList(property)
            .Select(StripPatternTags)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static RelatedTechnologyRule[] ReadRelatedRules(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return [];

        return ReadStringLikeList(property)
            .Select(raw =>
            {
                var parsed = TechnologyPatternParser.Parse(raw);
                return new RelatedTechnologyRule(parsed.RegexPattern.Trim(), parsed.Confidence, parsed.VersionExpression);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.TechnologyName))
            .DistinctBy(x => x.TechnologyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ReadStringLikeList(JsonElement property)
    {
        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                yield return property.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        yield return item.GetString() ?? "";
                }
                break;
            case JsonValueKind.Object:
                foreach (var item in property.EnumerateObject())
                    yield return item.Name;
                break;
        }
    }

    private static string StripPatternTags(string raw) => TechnologyPatternParser.Parse(raw).RegexPattern.Trim();
}
