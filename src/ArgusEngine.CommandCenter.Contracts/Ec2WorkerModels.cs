namespace ArgusEngine.CommandCenter.Contracts;

public record Ec2WorkerCountsDto(
    int Spider,
    int Enum,
    int PortScan,
    int HighValue,
    int TechnologyIdentification);

public record Ec2WorkerMachineDto(
    Guid Id,
    string Name,
    string? InstanceId,
    string AwsState,
    string? PublicIpAddress,
    string? PrivateIpAddress,
    string? InstanceType,
    string? LastCommandId,
    string? LastCommandStatus,
    string? StatusMessage,
    Ec2WorkerCountsDto Workers,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastAppliedAtUtc);

public record Ec2WorkerMachineMutationResult(
    Ec2WorkerMachineDto Machine,
    string Message);

public record Ec2WorkerMachineCreateRequest(
    string? Name,
    Ec2WorkerCountsDto? Workers);

public record Ec2WorkerMachineScaleRequest(
    Ec2WorkerCountsDto? Workers);
