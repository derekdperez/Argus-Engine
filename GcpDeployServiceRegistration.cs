using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArgusEngine.CloudDeploy;

public static class GcpDeployServiceRegistration
{
    /// <summary>
    /// Registers the GCP hybrid deploy service and its dependencies.
    /// Binds config from the "GcpDeploy" section of <paramref name="configuration"/>.
    /// </summary>
    /// <example>
    /// In Program.cs or a service registration file:
    /// <code>
    /// services.AddGcpHybridDeploy(builder.Configuration);
    /// </code>
    /// Then inject <see cref="IGcpHybridDeployService"/> anywhere.
    /// </example>
    public static IServiceCollection AddGcpHybridDeploy(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        services.Configure<GcpDeployOptions>(
            configuration.GetSection(GcpDeployOptions.Section));

        services.AddSingleton<GcpImageBuilder>();
        services.AddSingleton<CloudRunWorkerManager>();
        services.AddSingleton<LocalCoreOrchestrator>();
        services.AddSingleton<IGcpHybridDeployService, GcpHybridDeployService>();

        return services;
    }
}
