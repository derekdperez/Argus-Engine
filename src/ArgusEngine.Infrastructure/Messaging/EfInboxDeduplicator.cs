using ArgusEngine.Application.Events;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class EfInboxDeduplicator(ArgusDbContext db, ILogger logger) : IInboxDeduplicator
{
    private static readonly Action<ILogger, Guid, string, Exception?> LogDuplicateInboxEvent =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Debug,
            new EventId(1, nameof(LogDuplicateInboxEvent)),
            "Skipping duplicate inbox event {EventId} for consumer {Consumer}.");

    public async Task<bool> TryBeginProcessingAsync(
        IEventEnvelope envelope,
        string consumer,
        CancellationToken cancellationToken = default)
    {
        if (envelope.EventId == Guid.Empty)
        {
            return true;
        }

        var inboxMessageId = Guid.NewGuid();
        var processedAtUtc = DateTimeOffset.UtcNow;

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inbox_messages (id, event_id, consumer, processed_at_utc)
            VALUES ({inboxMessageId}, {envelope.EventId}, {consumer}, {processedAtUtc})
            ON CONFLICT (event_id, consumer) DO NOTHING;
            """, cancellationToken).ConfigureAwait(false);

        if (inserted > 0)
        {
            return true;
        }

        LogDuplicateInboxEvent(logger, envelope.EventId, consumer, null);
        return false;
    }
}
