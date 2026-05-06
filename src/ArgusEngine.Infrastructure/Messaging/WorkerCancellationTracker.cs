using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.Infrastructure.Messaging;

public sealed class WorkerCancellationTracker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkerCancellationTracker> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTasks = new();
    private readonly ConcurrentHashSet<Guid> _cancelledMessageIds = new();

    private static readonly Action<ILogger, Guid, Exception?> _logImmediateCancellation =
        LoggerMessage.Define<Guid>(LogLevel.Information, new EventId(1, nameof(CreateLinkedCts)), "Message {MessageId} was already cancelled, triggering immediate cancellation");

    private static readonly Action<ILogger, Exception?> _logSyncError =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, nameof(ExecuteAsync)), "Error syncing worker cancellations");

    private static readonly Action<ILogger, Guid, Exception?> _logAdministrativeCancellation =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(3, nameof(SyncCancellationsAsync)), "Cancelling active task for message {MessageId} per administrative request");

    public WorkerCancellationTracker(IServiceScopeFactory scopeFactory, ILogger<WorkerCancellationTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public CancellationTokenSource CreateLinkedCts(Guid messageId, CancellationToken externalToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _activeTasks[messageId] = cts;
        
        if (_cancelledMessageIds.Contains(messageId))
        {
            _logImmediateCancellation(_logger, messageId, null);
            cts.Cancel();
        }
        
        return cts;
    }

    public void Untrack(Guid messageId)
    {
        if (_activeTasks.TryRemove(messageId, out var cts))
        {
            cts.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncCancellationsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logSyncError(_logger, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SyncCancellationsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var cancelledIds = await db.WorkerCancellations.AsNoTracking()
            .Where(c => c.RequestedAtUtc >= since)
            .Select(c => c.MessageId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var id in cancelledIds)
        {
            _cancelledMessageIds.Add(id);
            if (_activeTasks.TryGetValue(id, out var cts) && !cts.IsCancellationRequested)
            {
                _logAdministrativeCancellation(_logger, id, null);
                cts.Cancel();
            }
        }
        
        // Clean up old cancellations from memory (keep 1h)
        // (Simple implementation: just replace the set)
        // In a real app we might want more efficient cleanup.
    }
}

internal sealed class ConcurrentHashSet<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, byte> _dict = new();
    public bool Add(T item) => _dict.TryAdd(item, 0);
    public bool Contains(T item) => _dict.ContainsKey(item);
    public void Clear() => _dict.Clear();
}
