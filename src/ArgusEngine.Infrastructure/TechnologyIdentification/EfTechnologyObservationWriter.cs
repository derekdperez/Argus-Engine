using ArgusEngine.Application.TechnologyIdentification.Fingerprints;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArgusEngine.Infrastructure.TechnologyIdentification;

public sealed class EfTechnologyObservationWriter(IDbContextFactory<ArgusDbContext> dbFactory) : ITechnologyObservationWriter
{
    public async Task<TechnologyObservationPersistenceResult> UpsertPassiveObservationsAsync(
        Guid targetId,
        IReadOnlyList<TechnologyObservationDraft> observations,
        string catalogHash,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var run = new TechnologyDetectionRun
        {
            Id = Guid.NewGuid(),
            TargetId = targetId,
            CatalogHash = catalogHash,
            Mode = "passive",
            Status = "running",
            CreatedAtUtc = now,
            StartedAtUtc = now,
        };

        db.TechnologyDetectionRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (observations.Count == 0)
        {
            run.Status = "completed";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new TechnologyObservationPersistenceResult(run.Id, 0, 0, 0, 0);
        }

        var keys = observations.Select(x => x.DedupeKey).Distinct(StringComparer.Ordinal).ToArray();
        var targetAssetIds = observations.Select(x => x.AssetId).Distinct().ToArray();
        var existing = await db.TechnologyObservations
            .Where(x => x.TargetId == targetId && targetAssetIds.Contains(x.AssetId) && keys.Contains(x.DedupeKey))
            .ToDictionaryAsync(x => $"{x.AssetId:N}|{x.DedupeKey}", cancellationToken)
            .ConfigureAwait(false);

        var created = 0;
        var updated = 0;
        var evidenceAdded = 0;

        foreach (var draft in observations)
        {
            var key = $"{draft.AssetId:N}|{draft.DedupeKey}";
            if (!existing.TryGetValue(key, out var observation))
            {
                observation = new TechnologyObservation
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    TargetId = draft.TargetId,
                    AssetId = draft.AssetId,
                    FingerprintId = draft.FingerprintId,
                    CatalogHash = draft.CatalogHash,
                    TechnologyName = draft.TechnologyName,
                    Vendor = draft.Vendor,
                    Product = draft.Product,
                    Version = draft.Version,
                    ConfidenceScore = draft.Confidence,
                    SourceType = draft.SourceType,
                    DetectionMode = draft.DetectionMode,
                    DedupeKey = draft.DedupeKey,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                };
                db.TechnologyObservations.Add(observation);
                existing[key] = observation;
                created++;
            }
            else
            {
                observation.RunId = run.Id;
                observation.CatalogHash = draft.CatalogHash;
                observation.FingerprintId = draft.FingerprintId;
                observation.ConfidenceScore = Math.Max(observation.ConfidenceScore, draft.Confidence);
                observation.LastSeenUtc = now;
                updated++;
            }

            var evidenceHashes = draft.Evidence.Select(x => x.EvidenceHash).Distinct(StringComparer.Ordinal).ToArray();
            var existingEvidenceHashes = observation.Id == Guid.Empty || evidenceHashes.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : await db.TechnologyObservationEvidence.AsNoTracking()
                    .Where(x => x.ObservationId == observation.Id && evidenceHashes.Contains(x.EvidenceHash))
                    .Select(x => x.EvidenceHash)
                    .ToHashSetAsync(StringComparer.Ordinal, cancellationToken)
                    .ConfigureAwait(false);

            foreach (var evidence in draft.Evidence)
            {
                if (!existingEvidenceHashes.Add(evidence.EvidenceHash))
                    continue;

                db.TechnologyObservationEvidence.Add(new TechnologyObservationEvidence
                {
                    Id = Guid.NewGuid(),
                    ObservationId = observation.Id,
                    SignalId = evidence.SignalId,
                    EvidenceType = evidence.EvidenceType,
                    EvidenceKey = evidence.EvidenceKey,
                    MatchedValueRedacted = evidence.MatchedValueRedacted,
                    EvidenceHash = evidence.EvidenceHash,
                    CreatedAtUtc = now,
                });
                evidenceAdded++;
            }
        }

        run.Status = "completed";
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new TechnologyObservationPersistenceResult(
            run.Id,
            observations.Count,
            created,
            updated,
            evidenceAdded);
    }
}
