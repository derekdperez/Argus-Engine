namespace ArgusEngine.CloudDeploy;

/// <summary>
/// All worker service types that can be deployed to Cloud Run.
/// The string value is used in Cloud Run service names and Docker image tags.
/// </summary>
public enum WorkerType
{
    Enumeration,
    Spider,
    HttpRequester,
    PortScan,
    HighValue,
    TechnologyIdentification,
}

public static class WorkerTypeExtensions
{
    /// <summary>Kebab-case slug used in Cloud Run service names and image tags.</summary>
    public static string ToSlug(this WorkerType worker) => worker switch
    {
        WorkerType.Enumeration            => "enumeration",
        WorkerType.Spider                 => "spider",
        WorkerType.HttpRequester          => "httprequester",
        WorkerType.PortScan               => "portscan",
        WorkerType.HighValue              => "highvalue",
        WorkerType.TechnologyIdentification => "techid",
        _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, null),
    };

    /// <summary>
    /// Relative path to the .csproj file for this worker, from the repo root.
    /// Used by the image builder to know which project to publish.
    /// </summary>
    public static string ToCsprojPath(this WorkerType worker) => worker switch
    {
        WorkerType.Enumeration            => "src/ArgusEngine.Workers.Enumeration/ArgusEngine.Workers.Enumeration.csproj",
        WorkerType.Spider                 => "src/ArgusEngine.Workers.Spider/ArgusEngine.Workers.Spider.csproj",
        WorkerType.HttpRequester          => "src/ArgusEngine.Workers.HttpRequester/ArgusEngine.Workers.HttpRequester.csproj",
        WorkerType.PortScan               => "src/ArgusEngine.Workers.PortScan/ArgusEngine.Workers.PortScan.csproj",
        WorkerType.HighValue              => "src/ArgusEngine.Workers.HighValue/ArgusEngine.Workers.HighValue.csproj",
        WorkerType.TechnologyIdentification => "src/ArgusEngine.Workers.TechnologyIdentification/ArgusEngine.Workers.TechnologyIdentification.csproj",
        _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, null),
    };

    /// <summary>All defined worker types as a convenient enumerable.</summary>
    public static IEnumerable<WorkerType> All() => Enum.GetValues<WorkerType>();
}
