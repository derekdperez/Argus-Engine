namespace NightmareV2.Contracts;

public record OpsSnapshotDto(
    AssetOpsSummaryDto AssetSummary,
    BusTrafficSummaryDto BusSummary,
    List<WorkerDetailStatsDto> WorkerStats);

public record AssetOpsSummaryDto(long TotalAssets, long NewToday, List<AssetCountByDomainDto> ByDomain);
public record AssetCountByDomainDto(string Domain, long Count);
public record BusTrafficSummaryDto(long PendingMessages, long FailedMessages, double MessagesPerSecond);
public record WorkerDetailStatsDto(string WorkerName, string Status, int InstanceCount, long ProcessedCount);
public record RabbitQueueBriefDto(string Name, long Messages);
public record WorkerActivitySnapshotDto(List<WorkerInstanceActivityDto> Instances);
public record WorkerInstanceActivityDto(string Id, string WorkerKind, string LastAction, DateTime Timestamp);
public record DockerRuntimeStatusDto(List<DockerContainerStatusDto> Containers, List<DockerImageStatusDto> Images);
public record DockerContainerStatusDto(string Id, string Names, string Image, string Status, string State);
public record DockerImageStatusDto(string Id, string Repository, string Tag, string Size);
public record DockerComponentHealthDto(string Name, bool IsHealthy, string Details);
public record WorkerKindSummaryDto(string Kind, int Count);
public record AssetGridRowDto(string Name, string Value, DateTime Discovered);
public record HttpRequestQueueRowDto(string Id, string Method, string Url, string State);
public record OpsOverviewDto(int ActiveWorkers, int PendingTasks);
public record HttpRequestQueueMetricsDto(int Total, int Pending, int Processing, int Failed);
public record HttpRequestQueueSettingsDto(int MaxConcurrency, bool Paused);
public record HighValueFindingRowDto(string Severity, string Title, string Target, DateTime Detected);