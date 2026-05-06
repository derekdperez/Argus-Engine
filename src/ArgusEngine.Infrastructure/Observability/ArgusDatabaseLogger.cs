using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Observability;

public sealed class ArgusDatabaseLoggerProvider : ILoggerProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _component;
    private readonly ConcurrentQueue<SystemError> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processorTask;

    public ArgusDatabaseLoggerProvider(IServiceProvider serviceProvider, string component)
    {
        _serviceProvider = serviceProvider;
        _component = component;
        _processorTask = Task.Run(ProcessQueueAsync);
    }

    public string Component => _component;

    public void EnqueueLog(SystemError error)
    {
        if (_queue.Count > 2000) return; // Circuit breaker to prevent OOM
        _queue.Enqueue(error);
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_queue.IsEmpty)
                {
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                var batch = new List<SystemError>();
                while (_queue.TryDequeue(out var error) && batch.Count < 100)
                {
                    batch.Add(error);
                }

                if (batch.Count > 0)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<ArgusDbContext>>();
                    if (dbFactory != null)
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(_cts.Token).ConfigureAwait(false);
                        db.SystemErrors.AddRange(batch);
                        await db.SaveChangesAsync(_cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Silently ignore logger failures to prevent recursive errors
                await Task.Delay(5000).ConfigureAwait(false);
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ArgusDatabaseLogger(categoryName, this);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _processorTask.Wait(2000); } catch { }
        _cts.Dispose();
    }
}

public sealed class ArgusDatabaseLogger : ILogger
{
    private readonly string _name;
    private readonly ArgusDatabaseLoggerProvider _provider;

    public ArgusDatabaseLogger(string name, ArgusDatabaseLoggerProvider provider)
    {
        _name = name;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        // Skip internal EF and logging related categories to avoid infinite loops
        if (_name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            _name.StartsWith("ArgusEngine.Infrastructure.Observability", StringComparison.Ordinal))
        {
            return;
        }

        var message = formatter(state, exception);
        
        var error = new SystemError
        {
            Component = _provider.Component,
            MachineName = Environment.MachineName,
            LogLevel = logLevel.ToString(),
            Message = message,
            Exception = exception?.ToString(),
            LoggerName = _name,
            Timestamp = DateTimeOffset.UtcNow
        };

        _provider.EnqueueLog(error);
    }
}
