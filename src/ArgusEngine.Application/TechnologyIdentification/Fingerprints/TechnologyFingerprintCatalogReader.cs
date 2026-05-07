using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArgusEngine.Application.TechnologyIdentification.Fingerprints;

public static class TechnologyFingerprintCatalogReader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LoadedTechnologyFingerprintCatalog LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Technology fingerprint catalog file was not found.",
                path);
        }

        using var hashStream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();

        using var jsonStream = File.OpenRead(path);
        var fingerprints = JsonSerializer.Deserialize<List<TechnologyFingerprintDefinition>>(jsonStream, JsonOptions)
            ?? throw new InvalidOperationException("Technology fingerprint catalog is empty or invalid.");

        var validation = TechnologyFingerprintCatalogValidator.Validate(fingerprints);
        if (!validation.IsValid)
        {
            throw new TechnologyFingerprintCatalogValidationException(validation);
        }

        return new LoadedTechnologyFingerprintCatalog(
            hash,
            fingerprints,
            path,
            validation);
    }

    public static LoadedTechnologyFingerprintCatalog LoadFromJson(string json, string resourcePath)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Technology fingerprint catalog is empty.");
        }

        var fingerprints = JsonSerializer.Deserialize<List<TechnologyFingerprintDefinition>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Technology fingerprint catalog is empty or invalid.");

        var validation = TechnologyFingerprintCatalogValidator.Validate(fingerprints);
        if (!validation.IsValid)
        {
            throw new TechnologyFingerprintCatalogValidationException(validation);
        }

        return new LoadedTechnologyFingerprintCatalog(
            ComputeSha256(json),
            fingerprints,
            resourcePath,
            validation);
    }

    public static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record LoadedTechnologyFingerprintCatalog(
    string CatalogHash,
    IReadOnlyList<TechnologyFingerprintDefinition> Fingerprints,
    string ResourcePath,
    TechnologyFingerprintCatalogValidationResult Validation);
