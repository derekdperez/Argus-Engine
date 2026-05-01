using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NightmareV2.CommandCenter.Hubs;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Domain.Entities;
using NightmareV2.Infrastructure.Data;

namespace NightmareV2.CommandCenter.Endpoints;

public static class Ec2WorkerEndpoints
{
    private const int MaxEc2WorkerMachines = 2;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ec2-workers")
            .WithTags("EC2 Workers");

        group.MapGet(
                "/machines",
                async (NightmareDbContext db, IConfiguration configuration, CancellationToken ct) =>
                {
                    await RefreshMachineStateAsync(db, configuration, ct).ConfigureAwait(false);
                    var rows = await db.Ec2WorkerMachines.AsNoTracking()
                        .OrderByDescending(m => m.CreatedAtUtc)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    return Results.Ok(rows.Select(ToDto).ToList());
                })
            .WithName("ListEc2WorkerMachines");

        group.MapPost(
                "/machines",
                async (
                    Ec2WorkerMachineCreateRequest body,
                    NightmareDbContext db,
                    IConfiguration configuration,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    var counts = NormalizeCounts(body.Workers);
                    if (TotalWorkers(counts) == 0)
                        return Results.BadRequest("At least one EC2 worker count must be greater than zero.");

                    await RefreshMachineStateAsync(db, configuration, ct).ConfigureAwait(false);
                    var activeCount = await db.Ec2WorkerMachines
                        .CountAsync(m => IsActiveMachineState(m.AwsState), ct)
                        .ConfigureAwait(false);
                    if (activeCount >= MaxEc2WorkerMachines)
                        return Results.BadRequest($"At most {MaxEc2WorkerMachines} EC2 worker machines are allowed at one time.");

                    var now = DateTimeOffset.UtcNow;
                    var name = NormalizeMachineName(body.Name, activeCount + 1);
                    var machine = new Ec2WorkerMachine
                    {
                        Id = NewId.NextGuid(),
                        Name = name,
                        AwsState = "pending",
                        SpiderWorkers = counts.Spider,
                        EnumWorkers = counts.Enum,
                        PortScanWorkers = counts.PortScan,
                        HighValueWorkers = counts.HighValue,
                        TechnologyIdentificationWorkers = counts.TechnologyIdentification,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };

                    db.Ec2WorkerMachines.Add(machine);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);

                    try
                    {
                        var launched = await LaunchMachineAsync(machine, counts, configuration, ct).ConfigureAwait(false);
                        ApplyInstanceState(machine, launched);
                        machine.StatusMessage = "EC2 worker machine launch requested. User data will bootstrap Docker and apply worker counts.";
                        machine.UpdatedAtUtc = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        await NotifyChangedAsync(hub, $"{machine.Name} EC2 worker machine launch requested", ct).ConfigureAwait(false);
                        return Results.Ok(new Ec2WorkerMachineMutationResult(ToDto(machine), machine.StatusMessage));
                    }
                    catch (Exception ex) when (ex is AmazonServiceException or InvalidOperationException)
                    {
                        machine.AwsState = "error";
                        machine.StatusMessage = ex.Message;
                        machine.UpdatedAtUtc = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        return Results.BadRequest(new Ec2WorkerMachineMutationResult(ToDto(machine), $"EC2 launch failed: {ex.Message}"));
                    }
                })
            .WithName("CreateEc2WorkerMachine");

        group.MapPut(
                "/machines/{id:guid}/scale",
                async (
                    Guid id,
                    Ec2WorkerMachineScaleRequest body,
                    NightmareDbContext db,
                    IConfiguration configuration,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    var machine = await db.Ec2WorkerMachines.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
                    if (machine is null)
                        return Results.NotFound();
                    if (string.IsNullOrWhiteSpace(machine.InstanceId))
                        return Results.BadRequest("EC2 worker machine does not have an AWS instance id yet.");

                    var counts = NormalizeCounts(body.Workers);
                    ApplyCounts(machine, counts);
                    machine.UpdatedAtUtc = DateTimeOffset.UtcNow;

                    try
                    {
                        var commandId = await SendApplyScaleCommandAsync(machine, counts, configuration, ct).ConfigureAwait(false);
                        machine.LastCommandId = commandId;
                        machine.LastCommandStatus = "Pending";
                        machine.LastAppliedAtUtc = DateTimeOffset.UtcNow;
                        machine.StatusMessage = "Scale command sent through AWS Systems Manager.";
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        await NotifyChangedAsync(hub, $"{machine.Name} EC2 worker scale command sent", ct).ConfigureAwait(false);
                        return Results.Ok(new Ec2WorkerMachineMutationResult(ToDto(machine), machine.StatusMessage));
                    }
                    catch (Exception ex) when (ex is AmazonServiceException or InvalidOperationException)
                    {
                        machine.LastCommandStatus = "Failed";
                        machine.StatusMessage = ex.Message;
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        return Results.BadRequest(new Ec2WorkerMachineMutationResult(ToDto(machine), $"EC2 scale command failed: {ex.Message}"));
                    }
                })
            .WithName("ScaleEc2WorkerMachine");

        group.MapDelete(
                "/machines/{id:guid}",
                async (
                    Guid id,
                    NightmareDbContext db,
                    IConfiguration configuration,
                    IHubContext<DiscoveryHub> hub,
                    CancellationToken ct) =>
                {
                    var machine = await db.Ec2WorkerMachines.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
                    if (machine is null)
                        return Results.NotFound();

                    if (!string.IsNullOrWhiteSpace(machine.InstanceId)
                        && !machine.AwsState.Equals("terminated", StringComparison.OrdinalIgnoreCase))
                    {
                        var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(region))
                            return Results.BadRequest("AWS region is not configured and could not be inferred from EC2 metadata.");

                        using var ec2 = new AmazonEC2Client(RegionEndpoint.GetBySystemName(region));
                        await ec2.TerminateInstancesAsync(
                                new TerminateInstancesRequest { InstanceIds = [machine.InstanceId] },
                                ct)
                            .ConfigureAwait(false);
                    }

                    var alreadyTerminal = string.IsNullOrWhiteSpace(machine.InstanceId)
                        || machine.AwsState.Equals("terminated", StringComparison.OrdinalIgnoreCase);
                    machine.AwsState = alreadyTerminal ? "terminated" : "terminating";
                    machine.StatusMessage = alreadyTerminal
                        ? "EC2 worker machine removed from active controls."
                        : "EC2 termination requested.";
                    machine.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await NotifyChangedAsync(hub, $"{machine.Name} EC2 worker machine termination requested", ct).ConfigureAwait(false);
                    return Results.Ok(new Ec2WorkerMachineMutationResult(ToDto(machine), machine.StatusMessage));
                })
            .WithName("RemoveEc2WorkerMachine");
    }

    private static async Task<Instance> LaunchMachineAsync(
        Ec2WorkerMachine machine,
        Ec2WorkerCountsDto counts,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("AWS region is not configured and could not be inferred from EC2 metadata.");

        using var ec2 = new AmazonEC2Client(RegionEndpoint.GetBySystemName(region));
        var imageId = await ResolveWorkerAmiAsync(ec2, configuration, ct).ConfigureAwait(false);
        var subnetId = await ResolveSubnetIdAsync(ec2, configuration, ct).ConfigureAwait(false);
        var securityGroupIds = await ResolveSecurityGroupIdsAsync(ec2, configuration, subnetId, ct).ConfigureAwait(false);
        var instanceType = configuration["EC2_WORKER_INSTANCE_TYPE"]
            ?? configuration["WORKER_INSTANCE_TYPE"]
            ?? "m7i-flex.large";

        var request = new RunInstancesRequest
        {
            ImageId = imageId,
            InstanceType = InstanceType.FindValue(instanceType),
            MinCount = 1,
            MaxCount = 1,
            UserData = Convert.ToBase64String(Encoding.UTF8.GetBytes(await BuildUserDataAsync(machine, counts, configuration, ct).ConfigureAwait(false))),
            TagSpecifications =
            [
                new TagSpecification
                {
                    ResourceType = Amazon.EC2.ResourceType.Instance,
                    Tags =
                    [
                        new Amazon.EC2.Model.Tag("Name", machine.Name),
                        new Amazon.EC2.Model.Tag("Purpose", "nightmare-ec2-worker"),
                        new Amazon.EC2.Model.Tag("NightmareEc2WorkerMachineId", machine.Id.ToString()),
                    ],
                },
            ],
        };

        if (!string.IsNullOrWhiteSpace(subnetId))
            request.SubnetId = subnetId;
        if (securityGroupIds.Count > 0)
            request.SecurityGroupIds = securityGroupIds;

        var keyName = configuration["EC2_WORKER_KEY_PAIR"] ?? configuration["WORKER_KEY_PAIR"];
        if (!string.IsNullOrWhiteSpace(keyName))
            request.KeyName = keyName;

        var profileName = configuration["EC2_WORKER_IAM_ROLE"] ?? configuration["WORKER_IAM_ROLE"];
        if (!string.IsNullOrWhiteSpace(profileName))
            request.IamInstanceProfile = new IamInstanceProfileSpecification { Name = profileName };

        var response = await ec2.RunInstancesAsync(request, ct).ConfigureAwait(false);
        return response.Reservation.Instances.Single();
    }

    private static async Task RefreshMachineStateAsync(NightmareDbContext db, IConfiguration configuration, CancellationToken ct)
    {
        var rows = await db.Ec2WorkerMachines
            .Where(m => m.InstanceId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0)
            return;

        var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            return;

        try
        {
            using var ec2 = new AmazonEC2Client(RegionEndpoint.GetBySystemName(region));
            var instances = await DescribeInstancesAsync(ec2, rows.Select(m => m.InstanceId!).ToList(), ct).ConfigureAwait(false);

            using var ssm = new AmazonSimpleSystemsManagementClient(RegionEndpoint.GetBySystemName(region));
            foreach (var row in rows)
            {
                if (instances.TryGetValue(row.InstanceId!, out var instance))
                {
                    ApplyInstanceState(row, instance);
                    row.UpdatedAtUtc = DateTimeOffset.UtcNow;
                }

                if (!string.IsNullOrWhiteSpace(row.LastCommandId))
                    await RefreshCommandStatusAsync(ssm, row, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (AmazonServiceException)
        {
            // Keep persisted state when AWS state is temporarily unavailable.
        }
    }

    private static async Task<Dictionary<string, Instance>> DescribeInstancesAsync(
        AmazonEC2Client ec2,
        List<string> instanceIds,
        CancellationToken ct)
    {
        if (instanceIds.Count == 0)
            return [];

        var response = await ec2.DescribeInstancesAsync(
                new DescribeInstancesRequest { InstanceIds = instanceIds },
                ct)
            .ConfigureAwait(false);

        return response.Reservations
            .SelectMany(r => r.Instances)
            .Where(i => !string.IsNullOrWhiteSpace(i.InstanceId))
            .ToDictionary(i => i.InstanceId, StringComparer.Ordinal);
    }

    private static async Task RefreshCommandStatusAsync(
        AmazonSimpleSystemsManagementClient ssm,
        Ec2WorkerMachine row,
        CancellationToken ct)
    {
        try
        {
            var response = await ssm.ListCommandInvocationsAsync(
                    new ListCommandInvocationsRequest
                    {
                        CommandId = row.LastCommandId,
                        InstanceId = row.InstanceId,
                        Details = false,
                    },
                    ct)
                .ConfigureAwait(false);
            var invocation = response.CommandInvocations.FirstOrDefault();
            if (invocation is null)
                return;

            row.LastCommandStatus = invocation.StatusDetails ?? invocation.Status?.Value;
            if (!string.IsNullOrWhiteSpace(invocation.StatusDetails))
                row.StatusMessage = $"Last SSM command: {invocation.StatusDetails}";
        }
        catch (AmazonSimpleSystemsManagementException)
        {
            // Command status is best-effort only.
        }
    }

    private static async Task<string> SendApplyScaleCommandAsync(
        Ec2WorkerMachine machine,
        Ec2WorkerCountsDto counts,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var region = await ResolveAwsRegionAsync(configuration, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("AWS region is not configured and could not be inferred from EC2 metadata.");

        var coreHost = await ResolveCoreHostAsync(configuration, ct).ConfigureAwait(false);
        var command = BuildApplyScaleCommand(machine, counts, configuration, coreHost, includeGitPull: true);
        using var ssm = new AmazonSimpleSystemsManagementClient(RegionEndpoint.GetBySystemName(region));
        var response = await ssm.SendCommandAsync(
                new SendCommandRequest
                {
                    DocumentName = "AWS-RunShellScript",
                    InstanceIds = [machine.InstanceId],
                    Comment = $"Apply NightmareV2 EC2 worker scale for {machine.Name}",
                    Parameters = new Dictionary<string, List<string>>
                    {
                        ["commands"] = [command],
                    },
                },
                ct)
            .ConfigureAwait(false);

        return response.Command.CommandId;
    }

    private static async Task<string> BuildUserDataAsync(
        Ec2WorkerMachine machine,
        Ec2WorkerCountsDto counts,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var repo = configuration["EC2_WORKER_REPOSITORY_URL"]
            ?? configuration["NIGHTMARE_REPOSITORY_URL"]
            ?? "https://github.com/derekdperez/NightmareV2.git";
        var branch = configuration["EC2_WORKER_GIT_BRANCH"]
            ?? configuration["NIGHTMARE_GIT_BRANCH"]
            ?? "main";
        var coreHost = await ResolveCoreHostAsync(configuration, ct).ConfigureAwait(false);
        var applyCommand = BuildApplyScaleCommand(machine, counts, configuration, coreHost, includeGitPull: false);

        return $$"""
                 #!/bin/bash
                 set -euo pipefail
                 export DEBIAN_FRONTEND=noninteractive
                 apt-get update
                 apt-get install -y curl wget git jq ca-certificates gnupg lsb-release apt-transport-https software-properties-common
                 mkdir -p /etc/apt/keyrings
                 curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
                 echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" > /etc/apt/sources.list.d/docker.list
                 apt-get update
                 apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
                 snap install amazon-ssm-agent --classic || apt-get install -y amazon-ssm-agent || true
                 systemctl enable docker
                 systemctl start docker
                 systemctl enable snap.amazon-ssm-agent.amazon-ssm-agent.service 2>/dev/null || systemctl enable amazon-ssm-agent 2>/dev/null || true
                 systemctl start snap.amazon-ssm-agent.amazon-ssm-agent.service 2>/dev/null || systemctl start amazon-ssm-agent 2>/dev/null || true
                 usermod -aG docker ubuntu || true
                 if [ ! -d /opt/nightmare/.git ]; then
                   rm -rf /opt/nightmare
                   git clone {{ShellQuote(repo)}} /opt/nightmare
                 fi
                 cd /opt/nightmare
                 git fetch origin
                 git checkout {{ShellQuote(branch)}}
                 git pull --ff-only origin {{ShellQuote(branch)}} || git pull origin {{ShellQuote(branch)}}
                 {{applyCommand}}
                 """;
    }

    private static string BuildApplyScaleCommand(
        Ec2WorkerMachine machine,
        Ec2WorkerCountsDto counts,
        IConfiguration configuration,
        string coreHost,
        bool includeGitPull)
    {
        var branch = configuration["EC2_WORKER_GIT_BRANCH"]
            ?? configuration["NIGHTMARE_GIT_BRANCH"]
            ?? "main";
        var envContent = BuildWorkerEnvContent(machine.Id, configuration, coreHost);
        var sb = new StringBuilder();
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine("cd /opt/nightmare");
        if (includeGitPull)
        {
            sb.AppendLine("git fetch origin");
            sb.AppendLine(FormattableString.Invariant($"git checkout {ShellQuote(branch)}"));
            sb.AppendLine(FormattableString.Invariant($"git pull --ff-only origin {ShellQuote(branch)} || git pull origin {ShellQuote(branch)}"));
        }

        sb.AppendLine("cat > deploy/ec2-worker.env <<'NIGHTMARE_EC2_WORKER_ENV'");
        sb.Append(envContent);
        sb.AppendLine("NIGHTMARE_EC2_WORKER_ENV");
        sb.AppendLine("chmod 600 deploy/ec2-worker.env || true");
        sb.AppendLine("chmod +x deploy/apply-ec2-worker-scale.sh || true");
        sb.AppendLine(FormattableString.Invariant(
            $"EC2_WORKER_SPIDER={counts.Spider} EC2_WORKER_ENUM={counts.Enum} EC2_WORKER_PORTSCAN={counts.PortScan} EC2_WORKER_HIGHVALUE={counts.HighValue} EC2_WORKER_TECHID={counts.TechnologyIdentification} ./deploy/apply-ec2-worker-scale.sh"));
        return sb.ToString();
    }

    private static string BuildWorkerEnvContent(Guid machineId, IConfiguration configuration, string coreHost)
    {
        var postgres = configuration["EC2_WORKER_POSTGRES"]
            ?? $"Host={coreHost};Port=5432;Database=nightmare_v2;Username=nightmare;Password=nightmare";
        var filestore = configuration["EC2_WORKER_FILESTORE"]
            ?? $"Host={coreHost};Port=5432;Database=nightmare_v2_files;Username=nightmare;Password=nightmare";
        var redis = configuration["EC2_WORKER_REDIS"] ?? $"{coreHost}:6379";
        var rabbitHost = configuration["EC2_WORKER_RABBITMQ_HOST"] ?? coreHost;
        var rabbitUser = configuration["EC2_WORKER_RABBITMQ_USERNAME"] ?? configuration["RabbitMq:Username"] ?? "nightmare";
        var rabbitPassword = configuration["EC2_WORKER_RABBITMQ_PASSWORD"] ?? configuration["RabbitMq:Password"] ?? "nightmare";
        var rabbitVhost = configuration["EC2_WORKER_RABBITMQ_VHOST"] ?? configuration["RabbitMq:VirtualHost"] ?? "/";
        var rabbitManagement = configuration["EC2_WORKER_RABBITMQ_MANAGEMENT_URL"] ?? $"http://{coreHost}:15672";

        var lines = new Dictionary<string, string>
        {
            ["NIGHTMARE_POSTGRES"] = postgres,
            ["NIGHTMARE_FILESTORE"] = filestore,
            ["NIGHTMARE_REDIS"] = redis,
            ["NIGHTMARE_RABBITMQ_HOST"] = rabbitHost,
            ["NIGHTMARE_RABBITMQ_USERNAME"] = rabbitUser,
            ["NIGHTMARE_RABBITMQ_PASSWORD"] = rabbitPassword,
            ["NIGHTMARE_RABBITMQ_VHOST"] = rabbitVhost,
            ["NIGHTMARE_RABBITMQ_MANAGEMENT_URL"] = rabbitManagement,
            ["NIGHTMARE_EC2_WORKER_MACHINE_ID"] = machineId.ToString(),
        };

        var sb = new StringBuilder();
        foreach (var (key, value) in lines)
            sb.Append(key).Append('=').AppendLine(EscapeEnvValue(value));
        return sb.ToString();
    }

    private static async Task<string> ResolveWorkerAmiAsync(AmazonEC2Client ec2, IConfiguration configuration, CancellationToken ct)
    {
        var configured = configuration["EC2_WORKER_AMI_ID"] ?? configuration["WORKER_AMI_ID"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var images = await ec2.DescribeImagesAsync(
                new DescribeImagesRequest
                {
                    Owners = ["099720109477"],
                    Filters =
                    [
                        new Filter("name", ["ubuntu/images/hvm-ssd/ubuntu-noble-24.04-amd64-server-*"]),
                        new Filter("state", ["available"]),
                    ],
                },
                ct)
            .ConfigureAwait(false);

        var latest = images.Images
            .OrderByDescending(i => i.CreationDate, StringComparer.Ordinal)
            .FirstOrDefault();
        return latest?.ImageId ?? throw new InvalidOperationException("Could not resolve an Ubuntu 24.04 AMI. Set EC2_WORKER_AMI_ID.");
    }

    private static async Task<string?> ResolveSubnetIdAsync(AmazonEC2Client ec2, IConfiguration configuration, CancellationToken ct)
    {
        var configured = configuration["EC2_WORKER_SUBNET_ID"] ?? configuration["WORKER_SUBNET_ID"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var vpcId = await ResolveVpcIdAsync(ec2, configuration, ct).ConfigureAwait(false);
        var subnets = await ec2.DescribeSubnetsAsync(
                new DescribeSubnetsRequest { Filters = [new Filter("vpc-id", [vpcId])] },
                ct)
            .ConfigureAwait(false);
        return subnets.Subnets.OrderBy(s => s.SubnetId, StringComparer.Ordinal).FirstOrDefault()?.SubnetId;
    }

    private static async Task<List<string>> ResolveSecurityGroupIdsAsync(
        AmazonEC2Client ec2,
        IConfiguration configuration,
        string? subnetId,
        CancellationToken ct)
    {
        var configured = configuration["EC2_WORKER_SECURITY_GROUP_IDS"] ?? configuration["WORKER_SECURITY_GROUP_IDS"];
        if (!string.IsNullOrWhiteSpace(configured))
            return SplitCsv(configured);

        var singleConfigured = configuration["EC2_WORKER_SECURITY_GROUP_ID"] ?? configuration["WORKER_SECURITY_GROUP_ID"];
        if (!string.IsNullOrWhiteSpace(singleConfigured))
            return [singleConfigured];

        var groupName = configuration["EC2_WORKER_SECURITY_GROUP"] ?? configuration["WORKER_SECURITY_GROUP"] ?? "default";
        var vpcId = await ResolveVpcIdAsync(ec2, configuration, ct).ConfigureAwait(false);
        var groups = await ec2.DescribeSecurityGroupsAsync(
                new DescribeSecurityGroupsRequest
                {
                    Filters =
                    [
                        new Filter("group-name", [groupName]),
                        new Filter("vpc-id", [vpcId]),
                    ],
                },
                ct)
            .ConfigureAwait(false);
        var groupId = groups.SecurityGroups.FirstOrDefault()?.GroupId;
        return string.IsNullOrWhiteSpace(groupId) ? [] : [groupId];
    }

    private static async Task<string> ResolveVpcIdAsync(AmazonEC2Client ec2, IConfiguration configuration, CancellationToken ct)
    {
        var configured = configuration["EC2_WORKER_VPC_ID"] ?? configuration["WORKER_VPC_ID"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var vpcs = await ec2.DescribeVpcsAsync(
                new DescribeVpcsRequest { Filters = [new Filter("is-default", ["true"])] },
                ct)
            .ConfigureAwait(false);
        return vpcs.Vpcs.FirstOrDefault()?.VpcId
            ?? throw new InvalidOperationException("Could not resolve a default VPC. Set EC2_WORKER_VPC_ID.");
    }

    private static void ApplyInstanceState(Ec2WorkerMachine machine, Instance instance)
    {
        machine.InstanceId = instance.InstanceId;
        machine.AwsState = instance.State?.Name?.Value ?? "unknown";
        machine.PublicIpAddress = instance.PublicIpAddress;
        machine.PrivateIpAddress = instance.PrivateIpAddress;
        machine.InstanceType = instance.InstanceType?.Value;
    }

    private static void ApplyCounts(Ec2WorkerMachine machine, Ec2WorkerCountsDto counts)
    {
        machine.SpiderWorkers = counts.Spider;
        machine.EnumWorkers = counts.Enum;
        machine.PortScanWorkers = counts.PortScan;
        machine.HighValueWorkers = counts.HighValue;
        machine.TechnologyIdentificationWorkers = counts.TechnologyIdentification;
    }

    private static Ec2WorkerCountsDto NormalizeCounts(Ec2WorkerCountsDto? counts) =>
        counts is null
            ? new Ec2WorkerCountsDto(0, 0, 0, 0, 0)
            : new Ec2WorkerCountsDto(
                Math.Max(0, counts.Spider),
                Math.Max(0, counts.Enum),
                Math.Max(0, counts.PortScan),
                Math.Max(0, counts.HighValue),
                Math.Max(0, counts.TechnologyIdentification));

    private static int TotalWorkers(Ec2WorkerCountsDto counts) =>
        counts.Spider + counts.Enum + counts.PortScan + counts.HighValue + counts.TechnologyIdentification;

    private static string NormalizeMachineName(string? requestedName, int ordinal)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? $"nightmare-ec2-worker-{ordinal}"
            : requestedName.Trim();
        return name.Length <= 128 ? name : name[..128];
    }

    private static bool IsActiveMachineState(string state) =>
        state is not ("terminated" or "terminating" or "shutting-down" or "error");

    private static Ec2WorkerMachineDto ToDto(Ec2WorkerMachine machine) =>
        new(
            machine.Id,
            machine.Name,
            machine.InstanceId,
            machine.AwsState,
            machine.PublicIpAddress,
            machine.PrivateIpAddress,
            machine.InstanceType,
            machine.LastCommandId,
            machine.LastCommandStatus,
            machine.StatusMessage,
            new Ec2WorkerCountsDto(
                machine.SpiderWorkers,
                machine.EnumWorkers,
                machine.PortScanWorkers,
                machine.HighValueWorkers,
                machine.TechnologyIdentificationWorkers),
            machine.CreatedAtUtc,
            machine.UpdatedAtUtc,
            machine.LastAppliedAtUtc);

    private static async Task<string?> ResolveAwsRegionAsync(IConfiguration configuration, CancellationToken ct)
    {
        var configured = configuration["AWS_REGION"]
            ?? configuration["AWS_DEFAULT_REGION"]
            ?? configuration["AWS:Region"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync("http://169.254.169.254/latest/dynamic/instance-identity/document", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("region", out var region) ? region.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ResolveCoreHostAsync(IConfiguration configuration, CancellationToken ct)
    {
        var configured = configuration["EC2_WORKER_CORE_HOST"] ?? configuration["NIGHTMARE_CORE_HOST"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync("http://169.254.169.254/latest/meta-data/local-ipv4", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var privateIp = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(privateIp))
                    return privateIp.Trim();
            }
        }
        catch
        {
            // Fall back below for non-EC2 local development.
        }

        return "host.docker.internal";
    }

    private static List<string> SplitCsv(string value) =>
        value.Replace(' ', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string EscapeEnvValue(string value) =>
        value.Contains('\n', StringComparison.Ordinal)
            ? value.Replace("\n", "", StringComparison.Ordinal)
            : value;

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static Task NotifyChangedAsync(IHubContext<DiscoveryHub> hub, string message, CancellationToken ct) =>
        hub.Clients.All.SendAsync(
            DiscoveryHubEvents.DomainEvent,
            new LiveUiEventDto("Ec2WorkersChanged", null, null, "workers", message, DateTimeOffset.UtcNow),
            cancellationToken: ct);
}
