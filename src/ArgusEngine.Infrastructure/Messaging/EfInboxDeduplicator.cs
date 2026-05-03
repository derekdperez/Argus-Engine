using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Events;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Persistence.Data;
using Npgsql;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class EfInboxDeduplicator(ArgusDbContext db, ILogger<EfInboxDeduplicator> logger) : IInboxDeduplicator
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
            return true;

        db.InboxMessages.Add(
            new InboxMessage
            {
                Id = Guid.NewGuid(),
                EventId = envelope.EventId,
                Consumer = consumer,
                ProcessedAtUtc = DateTimeOffset.UtcNow,
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            LogDuplicateInboxEvent(logger, envelope.EventId, consumer, null);
            return false;
        }
    }
}
