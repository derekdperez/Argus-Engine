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
        WriteIndented = false
    };

    public async Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : class, IEventEnvelope
    {
        await EnqueueBatchAsync(new[] { message }, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueBatchAsync<TEvent>(IEnumerable<TEvent> messages, CancellationToken cancellationToken = default)
        where TEvent : class, IEventEnvelope
    {
        var now = DateTimeOffset.UtcNow;
        var hasAny = false;

        foreach (var message in messages)
        {
            var resolvedEventId = message.EventId == Guid.Empty ? Guid.NewGuid() : message.EventId;
            var resolvedCorrelation = message.CorrelationId == Guid.Empty ? Guid.NewGuid() : message.CorrelationId;
            var resolvedCausation = message.CausationId == Guid.Empty ? resolvedCorrelation : message.CausationId;
            var resolvedOccurredAt = message.OccurredAtUtc == default ? now : message.OccurredAtUtc;
            var messageClrType = message.GetType();

            db.OutboxMessages.Add(
                new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    MessageType = OutboxMessageTypeRegistry.GetMessageKey(messageClrType),
                    PayloadJson = JsonSerializer.Serialize(message, messageClrType, JsonOptions),
                    EventId = resolvedEventId,
                    CorrelationId = resolvedCorrelation,
                    CausationId = resolvedCausation,
                    OccurredAtUtc = resolvedOccurredAt,
                    Producer = string.IsNullOrWhiteSpace(message.Producer) ? "argus-engine" : message.Producer,
                    State = OutboxMessageState.Pending,
                    AttemptCount = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    NextAttemptAtUtc = now,
                });
            hasAny = true;
        }

        if (hasAny)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
