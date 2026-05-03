using System.Diagnostics.Metrics;

namespace NightmareV2.Infrastructure.Observability;

public static class ArgusMeters
{
    public const string Name = "ArgusEngine";

    public static readonly Meter Meter = new(Name, "1.0.0");

    public static readonly Counter<long> AssetsDiscovered =
        Meter.CreateCounter<long>("argus_assets_discovered_total");

    public static readonly Counter<long> AssetAdmissionDecisions =
        Meter.CreateCounter<long>("argus_asset_admission_decisions_total");

    public static readonly Counter<long> FindingsCreated =
        Meter.CreateCounter<long>("argus_findings_created_total");

    public static readonly Counter<long> HttpRequestsCompleted =
        Meter.CreateCounter<long>("argus_http_requests_completed_total");

    public static readonly Histogram<double> HttpFetchDurationMs =
        Meter.CreateHistogram<double>("argus_http_fetch_duration_ms");

    public static readonly Counter<long> OutboxDispatched =
        Meter.CreateCounter<long>("argus_outbox_dispatched_total");

    public static readonly Counter<long> OutboxDeadLettered =
        Meter.CreateCounter<long>("argus_outbox_deadlettered_total");

    public static readonly Histogram<double> WorkerLoopDurationMs =
        Meter.CreateHistogram<double>("argus_worker_loop_duration_ms");

    public static readonly UpDownCounter<long> ActiveWorkerLeases =
        Meter.CreateUpDownCounter<long>("argus_active_worker_leases");

    public static readonly Counter<long> DataRetentionDeletedRows =
        Meter.CreateCounter<long>("argus_data_retention_deleted_rows_total");

    public static readonly Counter<long> DataRetentionArchivedRows =
        Meter.CreateCounter<long>("argus_data_retention_archived_rows_total");
}
