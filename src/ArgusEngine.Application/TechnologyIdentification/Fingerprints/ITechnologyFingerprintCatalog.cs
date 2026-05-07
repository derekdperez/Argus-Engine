namespace ArgusEngine.Application.TechnologyIdentification.Fingerprints;

public interface ITechnologyFingerprintCatalog
{
    string CatalogHash { get; }

    IReadOnlyList<TechnologyFingerprintDefinition> Fingerprints { get; }

    IReadOnlyDictionary<string, TechnologyFingerprintDefinition> ById { get; }

    IReadOnlyList<TechnologyFingerprintDefinition> FindByCapability(string capability);

    IReadOnlyList<TechnologyFingerprintDefinition> FindByTechnology(string technologyName);
}
