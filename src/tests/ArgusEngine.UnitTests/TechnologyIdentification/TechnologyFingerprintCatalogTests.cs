using ArgusEngine.Application.TechnologyIdentification.Fingerprints;
using FluentAssertions;
using Xunit;

namespace ArgusEngine.UnitTests.TechnologyIdentification;

public sealed class TechnologyFingerprintCatalogTests
{
    private static readonly Lazy<LoadedTechnologyFingerprintCatalog> LoadedCatalog = new(
        () => TechnologyFingerprintCatalogReader.LoadFromFile(GetCatalogPath()));

    [Fact]
    public void RuntimeCatalogFileExists()
    {
        File.Exists(GetCatalogPath()).Should().BeTrue();
    }

    [Fact]
    public void RuntimeCatalogLoadsAsTopLevelFingerprintArray()
    {
        LoadedCatalog.Value.Fingerprints.Should().NotBeEmpty();
        LoadedCatalog.Value.CatalogHash.Should().HaveLength(64);
    }

    [Fact]
    public void RuntimeCatalogFingerprintIdsAreUnique()
    {
        var duplicateIds = LoadedCatalog.Value.Fingerprints
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToArray();

        duplicateIds.Should().BeEmpty();
    }

    [Fact]
    public void RuntimeCatalogFingerprintsMeetRequiredSafetyShape()
    {
        var invalidFingerprints = LoadedCatalog.Value.Fingerprints
            .Where(x =>
                string.IsNullOrWhiteSpace(x.Id)
                || string.IsNullOrWhiteSpace(x.Source.Type)
                || string.IsNullOrWhiteSpace(x.Technology.Name)
                || !string.Equals(
                    x.RiskMode,
                    TechnologyFingerprintCatalogValidator.RequiredRiskMode,
                    StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToArray();

        invalidFingerprints.Should().BeEmpty();
    }

    [Fact]
    public void RuntimeCatalogPassesValidator()
    {
        var validation = TechnologyFingerprintCatalogValidator.Validate(LoadedCatalog.Value.Fingerprints);

        validation.IsValid.Should().BeTrue();
        validation.Errors.Should().BeEmpty();
        validation.InertFingerprintIds.Should().OnlyContain(id =>
            LoadedCatalog.Value.Fingerprints.Any(fingerprint =>
                string.Equals(fingerprint.Id, id, StringComparison.OrdinalIgnoreCase)
                && fingerprint.Signals.Count == 0
                && fingerprint.Probes.Count == 0));
    }

    private static string GetCatalogPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "Resources",
                "TechnologyDetection",
                "argus_fingerprints.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "TechnologyDetection",
            "argus_fingerprints.json"));
    }
}
