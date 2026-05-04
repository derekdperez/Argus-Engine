namespace ArgusEngine.CommandCenter.Services.Aws;

public sealed class EcsServiceNameResolver(IConfiguration configuration)
{
    public string ServiceNameForScaleKey(string scaleKey, string defaultServiceName)
    {
        var envName = scaleKey switch
        {
            "worker-spider" => "WORKER_SPIDER_SERVICE",
            "worker-enum" => "WORKER_ENUM_SERVICE",
            "worker-portscan" => "WORKER_PORTSCAN_SERVICE",
            "worker-highvalue" => "WORKER_HIGHVALUE_SERVICE",
            "worker-techid" => "WORKER_TECHID_SERVICE",
            _ => "",
        };

        return string.IsNullOrWhiteSpace(envName)
            ? defaultServiceName
            : configuration[envName] ?? defaultServiceName;
    }

    public string TaskFamilyForScaleKey(string scaleKey)
    {
        var envName = scaleKey switch
        {
            "worker-spider" => "ECS_TASK_FAMILY_WORKER_SPIDER",
            "worker-enum" => "ECS_TASK_FAMILY_WORKER_ENUM",
            "worker-portscan" => "ECS_TASK_FAMILY_WORKER_PORTSCAN",
            "worker-highvalue" => "ECS_TASK_FAMILY_WORKER_HIGHVALUE",
            "worker-techid" => "ECS_TASK_FAMILY_WORKER_TECHID",
            _ => "",
        };

        return string.IsNullOrWhiteSpace(envName)
            ? $"nightmare-v2-{scaleKey}"
            : configuration[envName] ?? $"nightmare-v2-{scaleKey}";
    }
}
