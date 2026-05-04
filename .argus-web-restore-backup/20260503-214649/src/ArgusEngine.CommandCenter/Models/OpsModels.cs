namespace ArgusEngine.CommandCenter.Models;

public sealed record OpsSummaryDto(
    DateTimeOffset AtUtc,
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
    long? OldestQueuedAgeSeconds,
    long FailedLastMinuteCount,
    long FailedLastHourCount,
    long FailedLast24HoursCount,
    long SentLastMinuteCount,
    long SentLastHourCount,
    long SentLast24HoursCount);

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
    string? LockedBy,
    DateTimeOffset? LockedUntilUtc,
    int? LastHttpStatus,
    string? LastError,
    string? RequestHeadersJson,
    string? RequestBody,
    string? ResponseHeadersJson,
    string? ResponseBody,
    long? DurationMs,
    string? ResponseContentType,
    long? ResponseContentLength,
    string? FinalUrl,
    int RedirectCount,
    string? RedirectChainJson);

public sealed record AssetGridRowDto(
    Guid Id,
    Guid TargetId,
    string Kind,
    string Category,
    string CanonicalKey,
    string RawValue,
    string? DisplayName,
    int Depth,
    string DiscoveredBy,
    string DiscoveryContext,
    DateTimeOffset DiscoveredAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    decimal Confidence,
    string LifecycleStatus,
    string? TypeDetailsJson,
    string? FinalUrl,
    int RedirectCount,
    string? RedirectChainJson);

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
    string? OriginalSourceUrl,
    string? FinalSourceUrl,
    int RedirectCount,
    string? RedirectChainJson,
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
    string Version,
    DateTimeOffset? FirstReleasedAtUtc,
    string Status,
    string Color,
    string Reason);

public sealed record DockerImageStatusDto(
    string Image,
    string Version,
    DateTimeOffset? FirstReleasedAtUtc,
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
    string Version,
    DateTimeOffset? ImageCreatedAtUtc,
    string DockerStatusText,
    string HealthCheckStatus,
    string Status,
    string Color,
    IReadOnlyList<string> LogTail);

public sealed record EcsRuntimeStatusDto(
    DateTimeOffset AtUtc,
    bool EcsAvailable,
    string ClusterName,
    string Status,
    string Color,
    string? Error,
    IReadOnlyList<EcsServiceStatusDto> Services);

public sealed record EcsServiceStatusDto(
    string WorkerKey,
    string ServiceName,
    string ServiceStatus,
    string Version,
    DateTimeOffset? FirstReleasedAtUtc,
    int DesiredCount,
    int RunningCount,
    int PendingCount,
    string TaskDefinition,
    string DeploymentStatus,
    string Color);

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

public sealed record WorkerScalingSettingsDto(
    string ScaleKey,
    string DisplayName,
    int MinTasks,
    int MaxTasks,
    int TargetBacklogPerTask,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkerScalingSettingsPatchDto(
    int MinTasks,
    int MaxTasks,
    int TargetBacklogPerTask);

public sealed record TechnologyDetectionRowDto(
    Guid Id,
    Guid TargetId,
    string TargetRootDomain,
    Guid AssetId,
    string AssetCanonicalKey,
    string TechnologyName,
    string CategoryName,
    string? Version,
    string EvidenceSource,
    string? EvidenceKey,
    string? Pattern,
    string? MatchedText,
    decimal Confidence,
    DateTimeOffset DetectedAtUtc);
