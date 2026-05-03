using System.Diagnostics.Metrics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NightmareV2.Infrastructure.Data;

namespace NightmareV2.Infrastructure.Observability;

public sealed class ArgusMetrics
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ArgusMetrics(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;

        ArgusMeters.Meter.CreateObservableGauge("argus_http_queue_depth", ObserveHttpQueueDepth);
        ArgusMeters.Meter.CreateObservableGauge("argus_outbox_depth", ObserveOutboxDepth);
        ArgusMeters.Meter.CreateObservableGauge("argus_findings_total_current", ObserveFindings);
        ArgusMeters.Meter.CreateObservableGauge("argus_assets_total_current", ObserveAssets);
    }

    private IEnumerable<Measurement<long>> ObserveHttpQueueDepth()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NightmareDbContext>();

        var rows = db.HttpRequestQueue
            .AsNoTracking()
            .GroupBy(x => x.State)
            .Select(x => new { State = x.Key, Count = x.LongCount() })
            .ToList();

        foreach (var row in rows)
        {
            yield return new Measurement<long>(
                row.Count,
                new KeyValuePair<string, object?>("state", row.State));
        }
    }

    private IEnumerable<Measurement<long>> ObserveOutboxDepth()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NightmareDbContext>();

        var rows = db.OutboxMessages
            .AsNoTracking()
            .GroupBy(x => x.State)
            .Select(x => new { State = x.Key, Count = x.LongCount() })
            .ToList();

        foreach (var row in rows)
        {
            yield return new Measurement<long>(
                row.Count,
                new KeyValuePair<string, object?>("state", row.State));
        }
    }

    private IEnumerable<Measurement<long>> ObserveFindings()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NightmareDbContext>();

        var rows = db.HighValueFindings
            .AsNoTracking()
            .GroupBy(x => x.Severity)
            .Select(x => new { Severity = x.Key, Count = x.LongCount() })
            .ToList();

        foreach (var row in rows)
        {
            yield return new Measurement<long>(
                row.Count,
                new KeyValuePair<string, object?>("severity", row.Severity));
        }
    }

    private IEnumerable<Measurement<long>> ObserveAssets()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NightmareDbContext>();

        var rows = db.Assets
            .AsNoTracking()
            .GroupBy(x => new { x.Kind, x.LifecycleStatus })
            .Select(x => new
            {
                x.Key.Kind,
                x.Key.LifecycleStatus,
                Count = x.LongCount()
            })
            .ToList();

        foreach (var row in rows)
        {
            yield return new Measurement<long>(
                row.Count,
                new KeyValuePair<string, object?>("kind", row.Kind.ToString()),
                new KeyValuePair<string, object?>("lifecycle_status", row.LifecycleStatus));
        }
    }
}
