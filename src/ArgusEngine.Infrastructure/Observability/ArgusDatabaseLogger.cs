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
    private volatile bool _schemaEnsured;

    public ArgusDatabaseLoggerProvider(IServiceProvider serviceProvider, string component)
    {
        _serviceProvider = serviceProvider;
        _component = string.IsNullOrWhiteSpace(component) ? "argus-engine" : component;
        _processorTask = Task.Run(ProcessQueueAsync);
    }

    public string Component => _component;

    public void EnqueueLog(SystemError error)
    {
        if (_queue.Count > 2000)
        {
            return; // Circuit breaker to prevent OOM if the database is unavailable.
        }

        _queue.Enqueue(error);
    }

    internal static async Task EnsureSystemErrorTableAsync(
        ArgusDbContext db,
        CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS system_errors (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Timestamp" timestamp with time zone NOT NULL,
                "Component" character varying(100) NOT NULL,
                "MachineName" character varying(100) NULL,
                "LogLevel" character varying(50) NOT NULL,
                "Message" text NOT NULL,
                "Exception" text NULL,
                "LoggerName" text NULL,
                "MetadataJson" jsonb NULL
            );

            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "Id" uuid;
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "Timestamp" timestamp with time zone NOT NULL DEFAULT now();
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "Component" character varying(100) NOT NULL DEFAULT '';
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "MachineName" character varying(100) NULL;
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "LogLevel" character varying(50) NOT NULL DEFAULT '';
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "Message" text NOT NULL DEFAULT '';
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "Exception" text NULL;
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "LoggerName" text NULL;
            ALTER TABLE system_errors ADD COLUMN IF NOT EXISTS "MetadataJson" jsonb NULL;

            CREATE INDEX IF NOT EXISTS ix_system_errors_timestamp ON system_errors ("Timestamp" DESC);
            CREATE INDEX IF NOT EXISTS ix_system_errors_component_timestamp ON system_errors ("Component", "Timestamp" DESC);
            CREATE INDEX IF NOT EXISTS ix_system_errors_log_level ON system_errors ("LogLevel");
            """,
            cancellationToken).ConfigureAwait(false);
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

                if (batch.Count == 0)
                {
                    continue;
                }

                using var scope = _serviceProvider.CreateScope();
                var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<ArgusDbContext>>();
                if (dbFactory is null)
                {
                    continue;
                }

                await using var db = await dbFactory.CreateDbContextAsync(_cts.Token).ConfigureAwait(false);

                if (!_schemaEnsured)
                {
                    await EnsureSystemErrorTableAsync(db, _cts.Token).ConfigureAwait(false);
                    _schemaEnsured = true;
                }

                db.SystemErrors.AddRange(batch);
                await db.SaveChangesAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Never let telemetry persistence failures create recursive logging failures.
                await Task.Delay(5000).ConfigureAwait(false);
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new ArgusDatabaseLogger(categoryName, this);

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _processorTask.Wait(2000);
        }
        catch
        {
            // Ignore shutdown races.
        }

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

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => default;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        // Skip internal EF and logging categories to avoid infinite logging recursion.
        if (_name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || _name.StartsWith("ArgusEngine.Infrastructure.Observability", StringComparison.Ordinal))
        {
            return;
        }

        var message = formatter(state, exception);
        if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
        {
            var eventName = string.IsNullOrWhiteSpace(eventId.Name) ? eventId.Id.ToString() : $"{eventId.Id}:{eventId.Name}";
            message = $"[{eventName}] {message}";
        }

        var error = new SystemError
        {
            Id = Guid.NewGuid(),
            Component = Truncate(_provider.Component, 100),
            MachineName = Truncate(Environment.MachineName, 100),
            LogLevel = Truncate(logLevel.ToString(), 50),
            Message = message ?? string.Empty,
            Exception = exception?.ToString(),
            LoggerName = _name,
            Timestamp = DateTimeOffset.UtcNow
        };

        _provider.EnqueueLog(error);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
