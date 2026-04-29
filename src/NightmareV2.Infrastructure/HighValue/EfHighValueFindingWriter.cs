using Microsoft.EntityFrameworkCore;
using NightmareV2.Application.HighValue;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.Infrastructure.HighValue;

public sealed class EfHighValueFindingWriter(NightmareDbContext db) : IHighValueFindingWriter
{
    public async Task<Guid> InsertFindingAsync(HighValueFindingInput input, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        db.HighValueFindings.Add(
            new HighValueFinding
            {
                Id = id,
                TargetId = input.TargetId,
                SourceAssetId = input.SourceAssetId,
                FindingType = input.FindingType,
                Severity = input.Severity,
                PatternName = input.PatternName,
                Category = input.Category,
                MatchedText = input.MatchedText,
                SourceUrl = input.SourceUrl,
                WorkerName = input.WorkerName,
                ImportanceScore = input.ImportanceScore,
                DiscoveredAtUtc = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }
}
