using Amazon;
using Amazon.ECS;
using AssignPublicIp = Amazon.ECS.AssignPublicIp;
using AwsVpcConfiguration = Amazon.ECS.Model.AwsVpcConfiguration;
using CreateServiceRequest = Amazon.ECS.Model.CreateServiceRequest;
using DeploymentConfiguration = Amazon.ECS.Model.DeploymentConfiguration;
using DescribeServicesRequest = Amazon.ECS.Model.DescribeServicesRequest;
using EcsService = Amazon.ECS.Model.Service;
using ListTaskDefinitionsRequest = Amazon.ECS.Model.ListTaskDefinitionsRequest;
using LaunchType = Amazon.ECS.LaunchType;
using NetworkConfiguration = Amazon.ECS.Model.NetworkConfiguration;
using SortOrder = Amazon.ECS.SortOrder;
using TaskDefinitionStatus = Amazon.ECS.TaskDefinitionStatus;
using UpdateServiceRequest = Amazon.ECS.Model.UpdateServiceRequest;
using ArgusEngine.Infrastructure.Configuration;

namespace ArgusEngine.CommandCenter.Services.Aws;

public sealed class EcsWorkerServiceManager(
    IConfiguration configuration,
    AwsRegionResolver regionResolver,
    EcsServiceNameResolver serviceNameResolver)
{
    public async Task<Dictionary<string, EcsService>> DescribeServicesAsync(
        IEnumerable<string> serviceNames,
        CancellationToken ct)
    {
        var region = await regionResolver.ResolveAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            return [];

        var cluster = configuration.GetArgusValue("Ecs:Cluster") ?? configuration["ECS_CLUSTER"] ?? "argus-engine";
        using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
        var response = await ecs.DescribeServicesAsync(
                new DescribeServicesRequest
                {
                    Cluster = cluster,
                    Services = serviceNames.Distinct(StringComparer.Ordinal).ToList(),
                },
                ct)
            .ConfigureAwait(false);

        return response.Services.ToDictionary(s => s.ServiceName, StringComparer.Ordinal);
    }

    public async Task UpdateDesiredCountAsync(
        string serviceName,
        int desiredCount,
        CancellationToken ct)
    {
        var region = await regionResolver.ResolveAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("AWS region is not configured and could not be inferred from EC2 metadata.");

        var cluster = configuration.GetArgusValue("Ecs:Cluster") ?? configuration["ECS_CLUSTER"] ?? "argus-engine";
        using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
        await ecs.UpdateServiceAsync(
                new UpdateServiceRequest
                {
                    Cluster = cluster,
                    Service = serviceName,
                    DesiredCount = desiredCount,
                },
                ct)
            .ConfigureAwait(false);
    }

    public async Task<(bool Changed, string Action)> EnsureWorkerServiceDesiredCountAsync(
        string scaleKey,
        string serviceName,
        int desiredCount,
        CancellationToken ct)
    {
        var region = await regionResolver.ResolveAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("AWS region is not configured and could not be inferred from EC2 metadata.");

        var cluster = configuration.GetArgusValue("Ecs:Cluster") ?? configuration["ECS_CLUSTER"] ?? "argus-engine";
        using var ecs = new AmazonECSClient(RegionEndpoint.GetBySystemName(region));
        var services = await ecs.DescribeServicesAsync(
                new DescribeServicesRequest
                {
                    Cluster = cluster,
                    Services = [serviceName],
                },
                ct)
            .ConfigureAwait(false);

        var service = services.Services.FirstOrDefault(s => string.Equals(s.ServiceName, serviceName, StringComparison.Ordinal));
        if (service is not null)
        {
            if (!string.Equals(service.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ECS service {serviceName} is {service.Status}; wait for it to become ACTIVE or finish deletion.");

            await ecs.UpdateServiceAsync(
                    new UpdateServiceRequest
                    {
                        Cluster = cluster,
                        Service = serviceName,
                        DesiredCount = desiredCount,
                    },
                    ct)
                .ConfigureAwait(false);

            return (true, "updated");
        }

        if (desiredCount == 0)
            return (false, "saved");

        var family = serviceNameResolver.TaskFamilyForScaleKey(scaleKey);
        var taskDefinitions = await ecs.ListTaskDefinitionsAsync(
                new ListTaskDefinitionsRequest
                {
                    FamilyPrefix = family,
                    Status = TaskDefinitionStatus.ACTIVE,
                    Sort = SortOrder.DESC,
                    MaxResults = 1,
                },
                ct)
            .ConfigureAwait(false);
        var taskDefinition = taskDefinitions.TaskDefinitionArns.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(taskDefinition))
            throw new InvalidOperationException($"No active ECS task definition was found for family {family}. Run deploy/aws/deploy-ecs-services.sh or ./deploy/deploy.sh --ecs-workers once to register it.");

        var subnets = SplitCsvConfiguration(configuration.GetArgusValue("Ecs:Subnets") ?? configuration["ECS_SUBNETS"]);
        var securityGroups = SplitCsvConfiguration(configuration.GetArgusValue("Ecs:SecurityGroups") ?? configuration["ECS_SECURITY_GROUPS"]);
        if (subnets.Count == 0 || securityGroups.Count == 0)
            throw new InvalidOperationException("ECS_SUBNETS (Argus:Ecs:Subnets) and ECS_SECURITY_GROUPS (Argus:Ecs:SecurityGroups) must be configured before Command Center can create worker services.");

        var request = new CreateServiceRequest
        {
            Cluster = cluster,
            ServiceName = serviceName,
            TaskDefinition = taskDefinition,
            DesiredCount = desiredCount,
            LaunchType = LaunchType.FindValue(configuration.GetArgusValue("Ecs:LaunchType") ?? configuration["ECS_LAUNCH_TYPE"] ?? "FARGATE"),
            DeploymentConfiguration = new DeploymentConfiguration
            {
                MinimumHealthyPercent = configuration.GetArgusValue<int?>("Ecs:MinHealthyPercent", null) ?? configuration.GetValue<int?>("ECS_MIN_HEALTHY_PERCENT") ?? 100,
                MaximumPercent = configuration.GetArgusValue<int?>("Ecs:MaxPercent", null) ?? configuration.GetValue<int?>("ECS_MAX_PERCENT") ?? 200,
            },
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration
                {
                    Subnets = subnets,
                    SecurityGroups = securityGroups,
                    AssignPublicIp = AssignPublicIp.FindValue(configuration.GetArgusValue("Ecs:AssignPublicIp") ?? configuration["ECS_ASSIGN_PUBLIC_IP"] ?? "DISABLED"),
                },
            },
        };

        if ((bool.TryParse(configuration.GetArgusValue("Ecs:EnableExecuteCommand"), out var enableArgus) && enableArgus)
            || (bool.TryParse(configuration["ECS_ENABLE_EXECUTE_COMMAND"], out var enableExecuteCommand) && enableExecuteCommand))
            request.EnableExecuteCommand = true;

        await ecs.CreateServiceAsync(request, ct).ConfigureAwait(false);
        return (true, "created");
    }

    public static string ExtractTaskDefinitionVersion(string? taskDefinition)
    {
        if (string.IsNullOrWhiteSpace(taskDefinition))
            return "-";

        var slash = taskDefinition.LastIndexOf('/');
        return slash >= 0 ? taskDefinition[(slash + 1)..] : taskDefinition;
    }

    private static List<string> SplitCsvConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Replace(' ', ',')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
}
