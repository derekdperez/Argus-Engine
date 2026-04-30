namespace NightmareV2.Application.TechnologyIdentification;

public sealed record TechnologyCatalog(
    IReadOnlyDictionary<string, TechnologyDefinition> Technologies,
    IReadOnlyDictionary<int, string> Categories,
    int FilesLoaded,
    int PatternsCompiled,
    int PatternsSkipped)
{
    public TechnologyDefinition? Find(string name) =>
        Technologies.TryGetValue(name, out var exact)
            ? exact
            : Technologies.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
