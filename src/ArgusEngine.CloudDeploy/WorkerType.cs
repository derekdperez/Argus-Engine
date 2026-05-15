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
        WorkerType.Enumeration => "enum",
        WorkerType.Spider => "spider",
        WorkerType.HttpRequester => "http-requester",
        WorkerType.PortScan => "portscan",
        WorkerType.HighValue => "highvalue",
        WorkerType.TechnologyIdentification => "techid",
        _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, null),
    };

    /// <summary>All defined worker types as a convenient enumerable.</summary>
    public static IEnumerable<WorkerType> All() => Enum.GetValues<WorkerType>();
}
