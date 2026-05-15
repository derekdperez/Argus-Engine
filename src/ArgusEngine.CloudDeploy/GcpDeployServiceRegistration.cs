using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CloudDeploy;

public static class GcpDeployServiceRegistration
{
    /// <summary>
    /// Registers the GCP hybrid deploy service and its dependencies.
    /// Binds and validates config from the "GcpDeploy" section.
    /// </summary>
    public static IServiceCollection AddGcpHybridDeploy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GcpDeployOptions.Section);
        var configuredOptions = section.Get<GcpDeployOptions>() ?? new GcpDeployOptions();
        var hasRequiredConfiguration =
            !string.IsNullOrWhiteSpace(configuredOptions.ProjectId) &&
            !string.IsNullOrWhiteSpace(configuredOptions.HostPublicAddress) &&
            !string.IsNullOrWhiteSpace(configuredOptions.RabbitMqPublicUrl);

        if (!hasRequiredConfiguration)
        {
            var issues = configuredOptions.Validate().ToArray();
            services.AddSingleton<IGcpHybridDeployService>(
                sp => new DisabledGcpHybridDeployService(
                    sp.GetRequiredService<ILogger<DisabledGcpHybridDeployService>>(),
                    issues));
            return services;
        }

        services
            .AddOptions<GcpDeployOptions>()
            .Bind(section)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<GcpDeployOptions>, GcpDeployOptionsValidator>();

        services.AddSingleton<GcpImageBuilder>();

        services.AddSingleton<CloudRunWorkerManager>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GcpDeployOptions>>();
            var imageBuilder = sp.GetRequiredService<GcpImageBuilder>();
            var logger = sp.GetRequiredService<ILogger<CloudRunWorkerManager>>();

            // Sync-over-async is constrained to singleton creation so the Cloud Run client
            // is initialized once instead of on every deploy/scale/status/teardown call.
            return CloudRunWorkerManager
                .CreateAsync(opts, imageBuilder, logger)
                .GetAwaiter()
                .GetResult();
        });

        services.AddSingleton<LocalCoreOrchestrator>();
        services.AddSingleton<IGcpHybridDeployService, GcpHybridDeployService>();

        return services;
    }

    private sealed class GcpDeployOptionsValidator(ILogger<GcpDeployOptions> logger)
        : IValidateOptions<GcpDeployOptions>
    {
        public ValidateOptionsResult Validate(string? name, GcpDeployOptions options)
        {
            var errors = options.Validate().ToList();
            if (errors.Count == 0)
                return ValidateOptionsResult.Success;

            foreach (var error in errors)
                logger.LogCritical("GcpDeploy misconfiguration: {Error}", error);

            return ValidateOptionsResult.Fail(errors);
        }
    }

    private sealed class DisabledGcpHybridDeployService(
        ILogger<DisabledGcpHybridDeployService> logger,
        IReadOnlyList<string> issues)
        : IGcpHybridDeployService
    {
        private const string BaseMessage =
            "GCP hybrid deploy is disabled because required GcpDeploy configuration is missing.";

        public Task<BulkDeployResult> BuildAndPushImagesAsync(
            IEnumerable<WorkerType>? workers = null,
            IProgress<DeployProgressEvent>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(FailedBulkResult(BaseMessage));

        public Task<BulkDeployResult> DeployWorkersAsync(
            IEnumerable<WorkerType>? workers = null,
            IProgress<DeployProgressEvent>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(FailedBulkResult(BaseMessage));

        public Task<CloudDeployResult> DeployWorkerAsync(
            WorkerType worker,
            IProgress<DeployProgressEvent>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(CloudDeployResult.Fail(BaseMessage));

        public Task<BulkDeployResult> ScaleWorkersAsync(
            int minInstances,
            int maxInstances,
            IEnumerable<WorkerType>? workers = null,
            CancellationToken ct = default) =>
            Task.FromResult(FailedBulkResult(BaseMessage));

        public Task<IReadOnlyList<WorkerStatus>> GetWorkerStatusesAsync(
            IEnumerable<WorkerType>? workers = null,
            CancellationToken ct = default)
        {
            var targetWorkers = workers?.ToArray() ?? WorkerTypeExtensions.All().ToArray();
            var statuses = targetWorkers
                .Select(worker => new WorkerStatus(
                    worker,
                    CloudDeployStatus.NotDeployed,
                    ServiceUrl: null,
                    CurrentInstances: 0,
                    MinInstances: 0,
                    MaxInstances: 0,
                    ImageUri: null,
                    LastError: BaseMessage))
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkerStatus>>(statuses);
        }

        public Task<BulkDeployResult> TeardownWorkersAsync(
            IEnumerable<WorkerType>? workers = null,
            IProgress<DeployProgressEvent>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(FailedBulkResult(BaseMessage));

        public Task<CloudDeployResult> StartLocalCoreAsync(
            IProgress<DeployProgressEvent>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(CloudDeployResult.Fail(BaseMessage));

        public Task<CloudDeployResult> StopLocalCoreAsync(
            IProgress<DeployProgressEvent>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(CloudDeployResult.Fail(BaseMessage));

        public Task<IReadOnlyList<string>> RunPreflightAsync(CancellationToken ct = default)
        {
            if (issues.Count == 0)
            {
                logger.LogWarning("{Message}", BaseMessage);
                return Task.FromResult<IReadOnlyList<string>>([BaseMessage]);
            }

            var all = new List<string>(capacity: issues.Count + 1) { BaseMessage };
            all.AddRange(issues);
            logger.LogWarning(
                "{Message} Issues: {Issues}",
                BaseMessage,
                string.Join("; ", all));
            return Task.FromResult<IReadOnlyList<string>>(all);
        }

        private static BulkDeployResult FailedBulkResult(string message) =>
            new(WorkerTypeExtensions.All()
                .Select(worker => (worker, CloudDeployResult.Fail(message)))
                .ToArray());
    }
}
