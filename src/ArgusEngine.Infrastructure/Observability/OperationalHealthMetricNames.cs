namespace ArgusEngine.Infrastructure.Observability;

public static class OperationalHealthMetricNames
{
    public const string HttpQueueOldestAgeSeconds = "argus_http_queue_oldest_age_seconds";
    public const string HttpQueueDepth = "argus_http_queue_depth";
    public const string OutboxDepth = "argus_outbox_depth";
    public const string WorkerDesiredCount = "argus_worker_desired_count";
    public const string WorkerRunningCount = "argus_worker_running_count";
    public const string DependencyHealth = "argus_dependency_health";
    public const string RealtimeUiEvents = "argus_realtime_ui_events_total";
    public const string ConfigAliasAccesses = "argus_config_alias_accesses_total";
    public const string OperationalAlerts = "argus_operational_alerts_total";
}
