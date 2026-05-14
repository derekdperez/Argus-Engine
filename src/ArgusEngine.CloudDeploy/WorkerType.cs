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
        WorkerType.Enumeration            => "enum",
        WorkerType.Spider                 => "spider",
        WorkerType.HttpRequester          => "http-requester",
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

    public static string ToProjectDir(this WorkerType worker) => worker switch
    {
        WorkerType.Enumeration            => "ArgusEngine.Workers.Enumeration",
        WorkerType.Spider                 => "ArgusEngine.Workers.Spider",
        WorkerType.HttpRequester          => "ArgusEngine.Workers.HttpRequester",
        WorkerType.PortScan               => "ArgusEngine.Workers.PortScan",
        WorkerType.HighValue              => "ArgusEngine.Workers.HighValue",
        WorkerType.TechnologyIdentification => "ArgusEngine.Workers.TechnologyIdentification",
        _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, null),
    };

    public static string ToAppDll(this WorkerType worker) => worker switch
    {
        WorkerType.Enumeration            => "ArgusEngine.Workers.Enumeration.dll",
        WorkerType.Spider                 => "ArgusEngine.Workers.Spider.dll",
        WorkerType.HttpRequester          => "ArgusEngine.Workers.HttpRequester.dll",
        WorkerType.PortScan               => "ArgusEngine.Workers.PortScan.dll",
        WorkerType.HighValue              => "ArgusEngine.Workers.HighValue.dll",
        WorkerType.TechnologyIdentification => "ArgusEngine.Workers.TechnologyIdentification.dll",
        _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, null),
    };

    /// <summary>All defined worker types as a convenient enumerable.</summary>
    public static IEnumerable<WorkerType> All() => Enum.GetValues<WorkerType>();
}
