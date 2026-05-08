namespace ArgusEngine.CommandCenter.Contracts;

public sealed record ComponentHealthDto(
    string Component,
    string Status,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record CommandCenterStatusDto(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyCollection<ComponentHealthDto> Components);

public sealed record WorkerSwitchDto(string WorkerKey, bool IsEnabled, DateTimeOffset UpdatedAtUtc);

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
    long DomainsWithFewerThanTenAssets,
    long SubdomainsDiscovered,
    DateTimeOffset? LastAssetCreatedAtUtc,
    DateTimeOffset? LastWorkerEventPublishedAtUtc,
    long HttpQueueQueuedAssetCount,
    long TechnologyObservationCount,
    long PublishedEventCount);

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
    string ActivityLabel,
    string Status,
    double? DurationMs,
    string? Error,
    Guid? MessageId);

public sealed record RabbitQueueBriefDto(
    string Name,
    long Messages,
    long MessagesReady,
    long MessagesUnacknowledged,
    int Consumers,
    string? LikelyWorkerKey);

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
