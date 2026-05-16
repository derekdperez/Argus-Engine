using Microsoft.EntityFrameworkCore;
using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Infrastructure.Data;

namespace ArgusEngine.CommandCenter.Maintenance.Api.Endpoints;

public static class SystemErrorsEndpoints
{
    public static IEndpointRouteBuilder MapSystemErrorsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/logs",
                async (ArgusDbContext db, string? level, string? component, string? search, int? take, int? minutes, CancellationToken ct) =>
                {
                    var limit = Math.Clamp(take ?? 200, 1, 1000);
                    var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 10080));
                    var since = DateTimeOffset.UtcNow - window;

                    var query = db.SystemErrors.AsNoTracking()
                        .Where(e => e.Timestamp >= since);

                    if (!string.IsNullOrWhiteSpace(level))
                    {
                        query = query.Where(e => e.LogLevel == level);
                    }

                    if (!string.IsNullOrWhiteSpace(component))
                    {
                        query = query.Where(e => e.Component.Contains(component));
                    }

                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        query = query.Where(e => e.Message.Contains(search) || (e.Exception != null && e.Exception.Contains(search)));
                    }

                    var rows = await query
                        .OrderByDescending(e => e.Timestamp)
                        .Take(limit)
                        .Select(e => new SystemErrorDto(
                            e.Id,
                            e.Timestamp,
                            e.Component,
                            e.MachineName ?? "",
                            e.LogLevel,
                            e.Message,
                            e.Exception ?? "",
                            e.LoggerName ?? "",
                            e.MetadataJson ?? ""))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("SystemErrors");

        app.MapGet(
                "/api/logs/components",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var components = await db.SystemErrors.AsNoTracking()
                        .Select(e => e.Component)
                        .Distinct()
                        .OrderBy(c => c)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(components);
                })
            .WithName("SystemErrorComponents");

        app.MapGet(
                "/api/logs/levels",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var levels = await db.SystemErrors.AsNoTracking()
                        .Select(e => e.LogLevel)
                        .Distinct()
                        .OrderBy(l => l)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(levels);
                })
            .WithName("SystemErrorLevels");

        app.MapGet(
                "/api/worker-logs",
                async (ArgusDbContext db, string? level, string? worker, string? search, int? take, int? minutes, CancellationToken ct) =>
                {
                    var limit = Math.Clamp(take ?? 200, 1, 1000);
                    var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 10080));
                    var since = DateTimeOffset.UtcNow - window;

                    var query = db.SystemErrors.AsNoTracking()
                        .Where(e => e.Timestamp >= since)
                        .Where(e => e.Component.StartsWith("worker-") || e.Component == "gatekeeper");

                    if (!string.IsNullOrWhiteSpace(level))
                    {
                        query = query.Where(e => e.LogLevel == level);
                    }

                    if (!string.IsNullOrWhiteSpace(worker))
                    {
                        query = query.Where(e => e.Component.Contains(worker));
                    }

                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        query = query.Where(e => e.Message.Contains(search) || (e.Exception != null && e.Exception.Contains(search)));
                    }

                    var rows = await query
                        .OrderByDescending(e => e.Timestamp)
                        .Take(limit)
                        .Select(e => new SystemErrorDto(
                            e.Id,
                            e.Timestamp,
                            e.Component,
                            e.MachineName ?? "",
                            e.LogLevel,
                            e.Message,
                            e.Exception ?? "",
                            e.LoggerName ?? "",
                            e.MetadataJson ?? ""))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    return Results.Ok(rows);
                })
            .WithName("WorkerLogs");

        app.MapGet(
                "/api/worker-logs/workers",
                async (ArgusDbContext db, CancellationToken ct) =>
                {
                    var workers = await db.SystemErrors.AsNoTracking()
                        .Where(e => e.Component.StartsWith("worker-") || e.Component == "gatekeeper")
                        .Select(e => e.Component)
                        .Distinct()
                        .OrderBy(c => c)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(workers);
                })
            .WithName("WorkerLogComponents");

        return app;
    }

    public static void Map(WebApplication app) => app.MapSystemErrorsEndpoints();
}

public record SystemErrorDto(
    Guid Id,
    DateTimeOffset Timestamp,
    string Component,
    string MachineName,
    string LogLevel,
    string Message,
    string Exception,
    string LoggerName,
    string MetadataJson);