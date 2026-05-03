using Microsoft.AspNetCore.Components;
using ArgusEngine.CommandCenter.DataMaintenance;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Observability;

using Radzen;

namespace ArgusEngine.CommandCenter.Startup;

public static class CommandCenterServiceRegistration
{
    public static IServiceCollection AddCommandCenterServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _ = environment;

        services.AddArgusObservability(configuration, "argus-command-center");

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddRadzenComponents();

        services.AddScoped(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
        });

        services.AddArgusInfrastructure(configuration);
        services.AddArgusRabbitMq(configuration, _ => { });
        services.AddSignalR();
        services.AddCommandCenterApplicationServices();

        return services;
    }

    private static IServiceCollection AddCommandCenterApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<HttpQueueArtifactBackfillService>();

        return services;
    }

}
