namespace NightmareV2.CommandCenter.Models;

public record RestartToolRequest(string[]? TargetIds, bool AllTargets);

public sealed record WorkerSwitchDto(string WorkerKey, bool IsEnabled, DateTimeOffset UpdatedAtUtc);

public sealed record WorkerPatchRequest(bool Enabled);

public sealed record WorkerScalePatchRequest(int DesiredCount);

public sealed record WorkerScaleTargetDto(
    string WorkerKey,
    string ScaleKey,
    string? EcsServiceName,
    int? DesiredCount,
    int? RunningCount,
    int? PendingCount,
    int? ManualDesiredCount,
    bool EcsConfigured,
    string Status);

public sealed record WorkerScaleUpdateResult(
    string WorkerKey,
    string ScaleKey,
    int DesiredCount,
    bool EcsUpdated,
    string Message);

public sealed record WorkerScaleOverrideDto(string ScaleKey, int DesiredCount);

public sealed record WorkerCapabilityDto(
    string WorkerKey,
    string DisplayName,
    string Version,
    bool SupportsTargetRestart,
    bool SupportsPauseResume,
    bool SupportsBacklogReplay,
    bool RequiresPrivilegedRuntime);

public sealed record WorkerHealthDto(
    string WorkerKey,
    bool Enabled,
    DateTimeOffset? LastActivityUtc,
    long ProcessedLastHour,
    long ProcessedLast24Hours,
    bool Healthy,
    string Reason);

public sealed record OpsOverviewDto(
    long TotalTargets,
    long TotalAssetsConfirmed,
    long TotalUrlAssets,
    long UrlsFromFetchedPages,
    long UrlsFromScripts,
    long UrlsGuessedWithWordlist,
    string? TopDomainByAssets,
    long TopDomainAssetCount,
    long DomainsWithTenOrMoreAssets,
    long DomainsWithFewerThanTenAssets);

public sealed record OpsSnapshotDto(
    IReadOnlyList<WorkerSwitchDto> Workers,
    WorkerActivitySnapshotDto Activity,
    AssetOpsSummaryDto AssetSummary,
    BusTrafficSummaryDto BusTraffic,
    IReadOnlyList<WorkerDetailStatsDto> WorkerStats,
    IReadOnlyList<RabbitQueueBriefDto> RabbitQueues,
    bool RabbitManagementAvailable);

public sealed record AssetOpsSummaryDto(
    long TotalAssets,
    long TotalTargets,
    long AssetsDiscoveredLastHour,
    long AssetsDiscoveredLast24Hours,
    DateTimeOffset? LastAssetDiscoveredAtUtc,
    long DiscoveredCount,
    long QueuedCount,
    long ConfirmedCount,
    long FetchableDiscoveredCount,
    long SubdomainCount,
    long DomainCount,
    long IpAddressCount,
    long UrlCount,
    long ConfirmedUrlCount,
    long HttpPipelineAssetCount,
    long ConfirmedHttpPipelineAssetCount,
    long HttpSnapshotsSavedCount,
    long OpenPortCount,
    long HighValueFindingCount,
    IReadOnlyList<AssetCountByDomainDto> TopDomains,
    IReadOnlyList<DiscoveredByCountDto> DiscoveredBy);

public sealed record AssetCountByDomainDto(string Domain, long Count);

public sealed record DiscoveredByCountDto(string DiscoveredBy, long Count);

public sealed record BusTrafficSummaryDto(
    long PublishedLastHour,
    long PublishedLast24Hours,
    long ConsumedLastHour,
    long ConsumedLast24Hours);

public sealed record WorkerDetailStatsDto(
    string WorkerKey,
    long ConsumedLastHour,
    long ConsumedLast24Hours,
    DateTimeOffset? LastConsumedAtUtc,
    long AssetsAttributedLastHour,
    long AssetsAttributedLast24Hours,
    long RabbitReadyMessages,
    long RabbitUnacknowledgedMessages,
    IReadOnlyList<string> RabbitQueues);

public sealed record WorkerActivitySnapshotDto(
    IReadOnlyList<WorkerKindSummaryDto> WorkerSummaries,
    IReadOnlyList<WorkerInstanceActivityDto> Instances);

public sealed record WorkerKindSummaryDto(
    string WorkerKey,
    bool ToggleEnabled,
    int InstanceCount,
    DateTimeOffset? LastActivityUtc,
    string RollupActivityLabel);

public sealed record WorkerInstanceActivityDto(
    string HostName,
    string WorkerKind,
    string ConsumerShortName,
    bool? ToggleEnabled,
    DateTimeOffset LastCompletedAtUtc,
    string MessageType,
    string PayloadPreview,
    string ActivityLabel);

public sealed record RabbitQueueBriefDto(
    string Name,
    long Messages,
    long MessagesReady,
    long MessagesUnacknowledged,
    int Consumers,
    string? LikelyWorkerKey);

public sealed record HttpRequestQueueSettingsDto(
    bool Enabled,
    int GlobalRequestsPerMinute,
    int PerDomainRequestsPerMinute,
    int MaxConcurrency,
    int RequestTimeoutSeconds,
    DateTimeOffset UpdatedAtUtc);

public sealed record HttpRequestQueueSettingsPatch(
    bool Enabled,
    int GlobalRequestsPerMinute,
    int PerDomainRequestsPerMinute,
    int MaxConcurrency,
    int RequestTimeoutSeconds);

public sealed record HttpRequestQueueMetricsDto(
    long QueuedCount,
    long RetryReadyCount,
    long ScheduledRetryCount,
    long InFlightCount,
    long FailedCount,
    long CompletedLastHourCount,
    long BacklogCount,
    DateTimeOffset? OldestQueuedAtUtc,
    long? OldestQueuedAgeSeconds);

public sealed record HttpRequestQueueRowDto(
    Guid Id,
    Guid AssetId,
    Guid TargetId,
    string AssetKind,
    string Method,
    string RequestUrl,
    string DomainKey,
    string State,
    int AttemptCount,
    int MaxAttempts,
    int Priority,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int? LastHttpStatus,
    string? LastError,
    long? DurationMs,
    string? ResponseContentType,
    long? ResponseContentLength,
    string? FinalUrl);

public sealed record AssetGridRowDto(
    Guid Id,
    Guid TargetId,
    string Kind,
    string CanonicalKey,
    string RawValue,
    int Depth,
    string DiscoveredBy,
    string DiscoveryContext,
    DateTimeOffset DiscoveredAtUtc,
    string LifecycleStatus);

public sealed record HighValueFindingRowDto(
    Guid Id,
    Guid TargetId,
    Guid? SourceAssetId,
    string FindingType,
    string Severity,
    string PatternName,
    string Category,
    string MatchedText,
    string SourceUrl,
    string WorkerName,
    int? ImportanceScore,
    DateTimeOffset DiscoveredAtUtc,
    string? TargetRootDomain);

public sealed record ReliabilitySloSnapshotDto(
    DateTimeOffset AtUtc,
    long PublishedLastHour,
    long ConsumedLastHour,
    decimal ConsumeSuccessRate,
    long HttpBacklogCount,
    long? OldestHttpBacklogAgeSeconds,
    long CompletedHttpLastHour,
    long FailedHttpLastHour,
    bool ApiReady);

public sealed record DockerRuntimeStatusDto(
    DateTimeOffset AtUtc,
    bool DockerAvailable,
    string Status,
    string Color,
    string? Error,
    IReadOnlyList<DockerComponentHealthDto> Components,
    IReadOnlyList<DockerImageStatusDto> Images,
    IReadOnlyList<DockerContainerStatusDto> Containers);

public sealed record DockerComponentHealthDto(
    string Key,
    string DisplayName,
    string Status,
    string Color,
    string Reason);

public sealed record DockerImageStatusDto(
    string Image,
    long ContainerCount,
    long HealthyContainers,
    long DegradedContainers,
    long CriticalContainers,
    string Status,
    string Color);

public sealed record DockerContainerStatusDto(
    string Id,
    string Name,
    string Image,
    string DockerStatusText,
    string HealthCheckStatus,
    string Status,
    string Color,
    IReadOnlyList<string> LogTail);

public sealed record AdminUsageSnapshotDto(
    DateTimeOffset AtUtc,
    DateTimeOffset MonthStartUtc,
    decimal EcsWorkerHoursMonthToDate,
    decimal EcsWorkerFreeTierHours,
    decimal EcsWorkerHoursRemaining,
    decimal EcsWorkerPercentUsed,
    decimal Ec2ServerHoursMonthToDate,
    long HttpRequestCountMonthToDate,
    long HttpTrafficBytesMonthToDate,
    long HttpRequestBytesMonthToDate,
    long HttpResponseBytesMonthToDate,
    long HttpTrafficBytesAllTime,
    DateTimeOffset? LastCloudUsageSampleAtUtc,
    IReadOnlyList<CloudUsageResourceDto> CloudResources);

public sealed record CloudUsageResourceDto(
    string ResourceKind,
    string ResourceId,
    string ResourceName,
    int LastRunningCount,
    DateTimeOffset? LastSampledAtUtc,
    decimal HoursMonthToDate);
