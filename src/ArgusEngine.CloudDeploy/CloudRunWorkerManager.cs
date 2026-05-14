using Google.Cloud.Run.V2;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CloudDeploy;

/// <summary>
/// Creates, updates, scales, and deletes Cloud Run services for each worker type
/// using the Google.Cloud.Run.V2 SDK directly — no gcloud CLI needed here.
/// </summary>
internal sealed class CloudRunWorkerManager(
    IOptions<GcpDeployOptions>          options,
    GcpImageBuilder                     imageBuilder,
    ILogger<CloudRunWorkerManager>      logger)
{
    private readonly GcpDeployOptions _opts = options.Value;

    // Cloud Run service name for a given worker
    private string ServiceName(WorkerType w) =>
        $"argus-worker-{w.ToSlug()}";

    // Full resource name: projects/{project}/locations/{region}/services/{name}
    private string ServiceResourceName(WorkerType w) =>
        $"projects/{_opts.ProjectId}/locations/{_opts.Region}/services/{ServiceName(w)}";

    // Parent for list/create calls
    private string Parent =>
        $"projects/{_opts.ProjectId}/locations/{_opts.Region}";

    // ── Deploy ────────────────────────────────────────────────────────────────

    public async Task<CloudDeployResult> DeployAsync(
        WorkerType                      worker,
        IProgress<DeployProgressEvent>? progress,
        CancellationToken               ct)
    {
        var imageUri = imageBuilder.GetImageUri(worker);
        var serviceName = ServiceName(worker);

        progress?.Report(new(worker, $"Deploying Cloud Run service: {serviceName}"));
        logger.LogInformation("Deploying worker {Worker} as service {Service}", worker, serviceName);

        var client = await ServicesClient.CreateAsync(cancellationToken: ct);
        var desiredService = BuildServiceSpec(worker, imageUri);

        try
        {
            // Try to get the existing service — update if it exists, create if not
            var existing = await client.GetServiceAsync(ServiceResourceName(worker), ct);

            progress?.Report(new(worker, $"Updating existing service {serviceName}..."));
            desiredService.Name = existing.Name;

            var updateOp = await client.UpdateServiceAsync(new UpdateServiceRequest
            {
                Service = desiredService
            });

            var updated = await updateOp.PollUntilCompletedAsync();
            var url = updated.Result.Uri;

            progress?.Report(new(worker, $"Service updated. URL: {url}"));
            return CloudDeployResult.Ok($"Updated {serviceName}", url);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            progress?.Report(new(worker, $"Creating new service {serviceName}..."));

            var createOp = await client.CreateServiceAsync(new CreateServiceRequest
            {
                Parent    = Parent,
                ServiceId = serviceName,
                Service   = desiredService,
            }, ct);

            var created = await createOp.PollUntilCompletedAsync();
            var url = created.Result.Uri;

            progress?.Report(new(worker, $"Service created. URL: {url}"));
            return CloudDeployResult.Ok($"Created {serviceName}", url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deploy worker {Worker}", worker);
            return CloudDeployResult.Fail($"Deploy failed for {serviceName}: {ex.Message}");
        }
    }

    // ── Scale ─────────────────────────────────────────────────────────────────

    public async Task<CloudDeployResult> ScaleAsync(
        WorkerType        worker,
        int               minInstances,
        int               maxInstances,
        CancellationToken ct)
    {
        var client = await ServicesClient.CreateAsync(cancellationToken: ct);
        var normalizedMin = Math.Clamp(minInstances, 0, 1000);
        var normalizedMax = Math.Clamp(Math.Max(maxInstances, Math.Max(normalizedMin, 1)), 1, 1000);

        try
        {
            var existing = await client.GetServiceAsync(ServiceResourceName(worker), ct);
            var currentImage = existing.Template?.Containers.FirstOrDefault()?.Image;
            var desiredService = BuildServiceSpec(worker, string.IsNullOrWhiteSpace(currentImage) ? imageBuilder.GetImageUri(worker) : currentImage);
            desiredService.Name = existing.Name;

            desiredService.Template.Scaling = new RevisionScaling
            {
                MinInstanceCount = normalizedMin,
                MaxInstanceCount = normalizedMax
            };

            var op = await client.UpdateServiceAsync(new UpdateServiceRequest
            {
                Service = desiredService
            });

            await op.PollUntilCompletedAsync();

            logger.LogInformation(
                "Scaled {Worker} to min={Min} max={Max}",
                worker, normalizedMin, normalizedMax);

            return CloudDeployResult.Ok(
                $"Scaled {ServiceName(worker)}: min={normalizedMin}, max={normalizedMax}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return CloudDeployResult.Fail($"{ServiceName(worker)} is not deployed yet.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            return CloudDeployResult.Fail($"Scale rejected for {ServiceName(worker)}: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            return CloudDeployResult.Fail($"Scale failed for {ServiceName(worker)}: {ex.Message}");
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public async Task<WorkerStatus> GetStatusAsync(WorkerType worker, CancellationToken ct)
    {
        var client = await ServicesClient.CreateAsync(cancellationToken: ct);

        try
        {
            var svc = await client.GetServiceAsync(ServiceResourceName(worker), ct);

            // Derive running instance count from the condition/traffic info.
            // The SDK doesn't expose live instance count directly; use 0 as a
            // placeholder — a real implementation could query Cloud Monitoring.
            var condition = svc.Conditions.FirstOrDefault(c => c.Type == "Ready");
            var status = condition?.State switch
            {
                Google.Cloud.Run.V2.Condition.Types.State.ConditionSucceeded => CloudDeployStatus.Running,
                Google.Cloud.Run.V2.Condition.Types.State.ConditionFailed    => CloudDeployStatus.Failed,
                _                                                             => CloudDeployStatus.Deploying,
            };

            return new WorkerStatus(
                Worker:           worker,
                Status:           status,
                ServiceUrl:       svc.Uri,
                CurrentInstances: 0,
                MinInstances:     svc.Template.Scaling?.MinInstanceCount ?? _opts.WorkerMinInstances,
                MaxInstances:     svc.Template.Scaling?.MaxInstanceCount ?? _opts.WorkerMaxInstances,
                ImageUri:         svc.Template.Containers.FirstOrDefault()?.Image,
                LastError:        status == CloudDeployStatus.Failed ? condition?.Message : null
            );
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return new WorkerStatus(
                Worker:           worker,
                Status:           CloudDeployStatus.NotDeployed,
                ServiceUrl:       null,
                CurrentInstances: 0,
                MinInstances:     0,
                MaxInstances:     0,
                ImageUri:         null,
                LastError:        null
            );
        }
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    public async Task<CloudDeployResult> TeardownAsync(WorkerType worker, CancellationToken ct)
    {
        var client = await ServicesClient.CreateAsync(cancellationToken: ct);

        try
        {
            var op = await client.DeleteServiceAsync(ServiceResourceName(worker), ct);
            await op.PollUntilCompletedAsync();

            logger.LogInformation("Deleted Cloud Run service {Service}", ServiceName(worker));
            return CloudDeployResult.Ok($"Deleted {ServiceName(worker)}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return CloudDeployResult.Ok($"{ServiceName(worker)} was not deployed — nothing to delete.");
        }
        catch (Exception ex)
        {
            return CloudDeployResult.Fail($"Teardown failed for {ServiceName(worker)}: {ex.Message}");
        }
    }

    // ── Spec builder ─────────────────────────────────────────────────────────

    private Service BuildServiceSpec(WorkerType worker, string imageUri)
    {
        var service = new Service
        {
            Template = new RevisionTemplate
            {
                Containers =
                {
                    new Container
                    {
                        Image = imageUri,
                        Resources = new ResourceRequirements
                        {
                            Limits =
                            {
                                ["cpu"]    = _opts.WorkerCpu,
                                ["memory"] = _opts.WorkerMemory,
                            },
                            CpuIdle = true,  // don't bill CPU while idle (scale-to-zero friendly)
                        },
                        // Workers listen on 8080 for Cloud Run health probes
                        Ports = { new ContainerPort { ContainerPort_ = 8080 } },
                    },
                },
                Scaling = new RevisionScaling
                {
                    MinInstanceCount = InitialMinInstances(worker),
                    MaxInstanceCount = Math.Max(_opts.WorkerMaxInstances, InitialMinInstances(worker)),
                },
                MaxInstanceRequestConcurrency = _opts.WorkerConcurrency,
            },
            // Allow unauthenticated invocations (workers are triggered by RabbitMQ,
            // not by HTTP callers — but Cloud Run still needs this for health probes
            // unless you use IAM-authenticated health checks).
            Ingress = IngressTraffic.InternalLoadBalancer,
        };

        service.Template.Containers[0].Env.AddRange(BuildEnvVars());
        return service;
    }

    private IEnumerable<EnvVar> BuildEnvVars()
    {
        // Core connectivity — workers need to reach the local host
        yield return Env("RabbitMq__Url",            _opts.RabbitMqPublicUrl);
        yield return Env("ConnectionStrings__Argus",  _opts.PostgresPublicUrl);
        yield return Env("Redis__Url",               _opts.RedisPublicUrl);
        yield return Env("CommandCenter__ApiBaseUrl", _opts.CommandCenterApiUrl);

        // Standard .NET / ASP.NET vars for Cloud Run
        yield return Env("ASPNETCORE_ENVIRONMENT", "Production");
        yield return Env("DOTNET_RUNNING_IN_CONTAINER", "true");
    }

    private static EnvVar Env(string name, string value) =>
        new() { Name = name, Value = value };

    private int InitialMinInstances(WorkerType worker) =>
        worker is WorkerType.Spider or WorkerType.Enumeration or WorkerType.HttpRequester
            ? Math.Max(8, _opts.WorkerMinInstances)
            : Math.Max(2, _opts.WorkerMinInstances);
}
