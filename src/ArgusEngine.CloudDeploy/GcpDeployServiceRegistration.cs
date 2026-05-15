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
        services
            .AddOptions<GcpDeployOptions>()
            .Bind(configuration.GetSection(GcpDeployOptions.Section))
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
}
