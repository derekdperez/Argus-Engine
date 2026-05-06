using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ArgusEngine.Infrastructure.Observability;

public static class ArgusObservabilityExtensions
{
    public static IServiceCollection AddArgusObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        var serviceVersion =
            Environment.GetEnvironmentVariable("ARGUS_BUILD_STAMP")
            ?? Environment.GetEnvironmentVariable("NIGHTMARE_BUILD_STAMP")
            ?? "unknown";

        services.AddSingleton<ArgusMetrics>();
        services.AddSingleton<ArgusTracing>();

        services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService(
                    serviceName: serviceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: $"{Environment.MachineName}:{Environment.ProcessId}");
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ArgusMeters.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation();

                var endpoint = configuration["OpenTelemetry:OtlpEndpoint"];
                if (!string.IsNullOrWhiteSpace(endpoint))
                    metrics.AddOtlpExporter();
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ArgusTracing.ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation();

                var endpoint = configuration["OpenTelemetry:OtlpEndpoint"];
                if (!string.IsNullOrWhiteSpace(endpoint))
                    tracing.AddOtlpExporter();
            });
        
        return services;
    }

    public static IServiceCollection AddArgusDatabaseLogging(
        this IServiceCollection services,
        string componentName)
    {
        services.AddSingleton<ILoggerProvider>(sp => 
            new ArgusDatabaseLoggerProvider(sp, componentName));
        
        return services;
    }
}
