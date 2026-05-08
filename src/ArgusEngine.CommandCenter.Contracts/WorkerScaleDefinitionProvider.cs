using ArgusEngine.Application.Workers;
using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.Contracts;

public sealed record WorkerScaleDefinition(string ScaleKey, string DefaultServiceName, string DisplayName);

public sealed record WorkerScaleTargetDefinition(string ScaleKey, string DefaultServiceName);

public sealed class WorkerScaleDefinitionProvider
{
    private static readonly string[] RequiredKeys =
    [
        WorkerKeys.Gatekeeper,
        WorkerKeys.Spider,
        WorkerKeys.Enumeration,
        WorkerKeys.PortScan,
        WorkerKeys.HighValueRegex,
        WorkerKeys.HighValuePaths,
        WorkerKeys.TechnologyIdentification,
    ];

    private static readonly WorkerScaleDefinition[] ScaleDefinitions =
    [
        new("worker-spider", "nightmare-worker-spider", "Spider Worker"),
        new("worker-enum", "nightmare-worker-enum", "Subdomain Enum Worker"),
        new("worker-portscan", "nightmare-worker-portscan", "Port Scan Worker"),
        new("worker-highvalue", "nightmare-worker-highvalue", "High Value Worker"),
        new("worker-techid", "nightmare-worker-techid", "Technology Identification Worker"),
    ];

    public IReadOnlyList<string> RequiredWorkerKeys => RequiredKeys;

    public IReadOnlyList<WorkerScaleDefinition> WorkerScaleDefinitions => ScaleDefinitions;

    public WorkerScaleTargetDefinition? GetScaleTargetForWorkerKey(string workerKey) =>
        workerKey switch
        {
            WorkerKeys.Spider => new("worker-spider", "nightmare-worker-spider"),
            WorkerKeys.Enumeration => new("worker-enum", "nightmare-worker-enum"),
            WorkerKeys.PortScan => new("worker-portscan", "nightmare-worker-portscan"),
            WorkerKeys.HighValueRegex or WorkerKeys.HighValuePaths => new("worker-highvalue", "nightmare-worker-highvalue"),
            WorkerKeys.TechnologyIdentification => new("worker-techid", "nightmare-worker-techid"),
            _ => null,
        };

    public WorkerScalingSettingsDto DefaultWorkerScalingSetting(string scaleKey)
    {
        var displayName = ScaleDefinitions.FirstOrDefault(d => d.ScaleKey == scaleKey)?.DisplayName ?? scaleKey;
        return scaleKey switch
        {
            "worker-spider" => new(scaleKey, displayName, 1, 50, 100, DateTimeOffset.UnixEpoch),
            "worker-enum" => new(scaleKey, displayName, 1, 20, 25, DateTimeOffset.UnixEpoch),
            "worker-portscan" => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
            "worker-highvalue" => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
            "worker-techid" => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
            _ => new(scaleKey, displayName, 1, 20, 100, DateTimeOffset.UnixEpoch),
        };
    }
}



