using ArgusEngine.Application.TechnologyIdentification.Fingerprints;
using ArgusEngine.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.TechnologyIdentification;

public sealed partial class ResourceTechnologyFingerprintCatalog : ITechnologyFingerprintCatalog
{
    public ResourceTechnologyFingerprintCatalog(
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<ResourceTechnologyFingerprintCatalog> logger)
    {
        ResourcePath = ResolveCatalogPath(environment, configuration);

        var loaded = TechnologyFingerprintCatalogReader.LoadFromFile(ResourcePath);
        CatalogHash = loaded.CatalogHash;
        Fingerprints = loaded.Fingerprints;
        Validation = loaded.Validation;
        ById = Fingerprints.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        LogCatalogLoaded(logger, Fingerprints.Count, ResourcePath, CatalogHash);

        if (Validation.UnsupportedCapabilities.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            LogUnsupportedCapabilities(
                logger,
                string.Join(", ", Validation.UnsupportedCapabilities.Take(20)),
                Validation.UnsupportedCapabilities.Count);
        }

        if (Validation.InertFingerprintIds.Count > 0 && logger.IsEnabled(LogLevel.Information))
        {
            LogInertFingerprints(
                logger,
                Validation.InertFingerprintIds.Count);
        }
    }

    public string CatalogHash { get; }

    public string ResourcePath { get; }

    public TechnologyFingerprintCatalogValidationResult Validation { get; }

    public IReadOnlyList<TechnologyFingerprintDefinition> Fingerprints { get; }

    public IReadOnlyDictionary<string, TechnologyFingerprintDefinition> ById { get; }

    public IReadOnlyList<TechnologyFingerprintDefinition> FindByCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return [];
        }

        return Fingerprints
            .Where(x => x.RequiredCapabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public IReadOnlyList<TechnologyFingerprintDefinition> FindByTechnology(string technologyName)
    {
        if (string.IsNullOrWhiteSpace(technologyName))
        {
            return [];
        }

        return Fingerprints
            .Where(x => string.Equals(x.Technology.Name, technologyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string ResolveCatalogPath(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath =
            configuration.GetArgusValue("TechnologyDetection:FingerprintCatalogPath")
            ?? configuration.GetArgusValue("TechnologyDetection:CatalogPath");

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var relative = Path.Combine("Resources", "TechnologyDetection", "argus_fingerprints.json");
        var candidates = new[]
        {
            Path.Combine(environment.ContentRootPath, relative),
            Path.Combine(environment.ContentRootPath, "..", "Resources", "TechnologyDetection", "argus_fingerprints.json"),
            Path.Combine(AppContext.BaseDirectory, relative),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Resources", "TechnologyDetection", "argus_fingerprints.json"),
        };

        var existing = candidates.FirstOrDefault(File.Exists);
        return existing is null ? candidates[0] : Path.GetFullPath(existing);
    }

    [LoggerMessage(
        EventId = 550001,
        Level = LogLevel.Information,
        Message = "Loaded {Count} technology fingerprints from {Path}. Hash={Hash}")]
    private static partial void LogCatalogLoaded(
        ILogger logger,
        int count,
        string path,
        string hash);

    [LoggerMessage(
        EventId = 550002,
        Level = LogLevel.Warning,
        Message = "Technology fingerprint catalog includes {Count} unsupported capabilities. First values: {Capabilities}")]
    private static partial void LogUnsupportedCapabilities(
        ILogger logger,
        string capabilities,
        int count);

    [LoggerMessage(
        EventId = 550003,
        Level = LogLevel.Information,
        Message = "Technology fingerprint catalog includes {Count} inert identity fingerprints with no executable signals or probes.")]
    private static partial void LogInertFingerprints(
        ILogger logger,
        int count);
}
