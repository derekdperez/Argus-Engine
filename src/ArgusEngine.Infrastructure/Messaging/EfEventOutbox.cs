using System.Text.Json;
using ArgusEngine.Application.Events;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class EfEventOutbox(ArgusDbContext db) : IEventOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public async Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class, IEventEnvelope
    {
        var messageType = message.GetType();

        var resolvedEventId = message.EventId == Guid.Empty ? Guid.NewGuid() : message.EventId;
        var resolvedCorrelationId = message.CorrelationId == Guid.Empty ? Guid.NewGuid() : message.CorrelationId;
        var resolvedCausationId = message.CausationId == Guid.Empty ? resolvedCorrelationId : message.CausationId;
        var occurredAt = message.OccurredAtUtc == default ? DateTimeOffset.UtcNow : message.OccurredAtUtc;
        var producer = string.IsNullOrWhiteSpace(message.Producer) ? "argus-engine" : message.Producer;

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageType = OutboxMessageTypeRegistry.GetMessageKey(messageType),
            PayloadJson = JsonSerializer.Serialize(message, messageType, JsonOptions),
            EventId = resolvedEventId,
            CorrelationId = resolvedCorrelationId,
            CausationId = resolvedCausationId,
            OccurredAtUtc = occurredAt,
            Producer = producer,
            State = OutboxMessageState.Pending,
            AttemptCount = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            NextAttemptAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
