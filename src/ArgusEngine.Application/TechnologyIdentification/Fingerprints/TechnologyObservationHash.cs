using System.Security.Cryptography;
using System.Text;

namespace ArgusEngine.Application.TechnologyIdentification.Fingerprints;

public static class TechnologyObservationHash
{
    public static string BuildDedupeKey(
        Guid targetId,
        Guid assetId,
        string technologyName,
        string? vendor,
        string? product,
        string? version,
        string sourceType) =>
        Sha256(string.Join(
            "|",
            targetId.ToString("N"),
            assetId.ToString("N"),
            Normalize(technologyName),
            Normalize(vendor),
            Normalize(product),
            Normalize(version),
            Normalize(sourceType)));

    public static string BuildEvidenceHash(
        string fingerprintId,
        string signalId,
        string evidenceType,
        string? evidenceKey,
        string? matchedValue) =>
        Sha256(string.Join(
            "|",
            Normalize(fingerprintId),
            Normalize(signalId),
            Normalize(evidenceType),
            Normalize(evidenceKey),
            Normalize(matchedValue)));

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
