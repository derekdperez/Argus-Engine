using Microsoft.EntityFrameworkCore;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Persistence.Data;

namespace ArgusEngine.Infrastructure.HighValue;

public sealed class EfHighValueFindingWriter(ArgusDbContext db) : IHighValueFindingWriter
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
