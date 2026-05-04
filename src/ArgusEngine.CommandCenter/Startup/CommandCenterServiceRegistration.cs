using MassTransit;

using Microsoft.AspNetCore.Components;
using ArgusEngine.CommandCenter.DataMaintenance;

using ArgusEngine.Application.Sagas;
using ArgusEngine.CommandCenter.Realtime;
using ArgusEngine.CommandCenter.Services.Aws;
using ArgusEngine.CommandCenter.Services.Targets;
using ArgusEngine.CommandCenter.Services.Workers;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
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

        OpsSnapshotBuilder.RegisterHttpClient(services);

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddRadzenComponents();

        services.AddScoped(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
        });

        services.AddArgusInfrastructure(configuration);
        services.AddSignalR();

        services.AddScoped<DiscoveryRealtimeClient>();

        services.AddCommandCenterApplicationServices();
        services.AddCommandCenterOptions();

        services.AddArgusRabbitMq(configuration, consumers =>
        {
            consumers.AddConsumer<TargetCreatedUiEventConsumer>();
            consumers.AddConsumer<AssetDiscoveredUiEventConsumer>();
            consumers.AddConsumer<ScannableContentAvailableUiEventConsumer>();
            consumers.AddConsumer<CriticalHighValueFindingAlertUiEventConsumer>();
            consumers.AddConsumer<PortScanRequestedUiEventConsumer>();
            consumers.AddConsumer<SubdomainEnumerationRequestedUiEventConsumer>();

            consumers.AddSagaStateMachine<TargetScanStateMachine, TargetScanState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
                    r.ExistingDbContext<NightmareDbContext>();
                });
        });

        return services;
    }

    private static IServiceCollection AddCommandCenterApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<TargetManagementService>();
        services.AddScoped<TargetSummaryQueryService>();
        services.AddScoped<RootSpiderSeedService>();
        services.AddScoped<HttpQueueArtifactBackfillService>();

        services.AddSingleton<WorkerScaleDefinitionProvider>();
        services.AddScoped<WorkerScalingSettingsService>();
        services.AddScoped<WorkerSwitchService>();

        services.AddScoped<AwsRegionResolver>();
        services.AddScoped<EcsWorkerServiceManager>();
        services.AddScoped<EcsServiceNameResolver>();

        return services;
    }

    private static IServiceCollection AddCommandCenterOptions(this IServiceCollection services)
    {
        services.AddOptions<NightmareRuntimeOptions>()
            .Configure<IConfiguration>((options, cfg) =>
            {
                cfg.GetSection("Nightmare").Bind(options);
                cfg.GetSection("Argus").Bind(options);
            })
            .Validate(
                o => !o.Diagnostics.Enabled || !string.IsNullOrWhiteSpace(o.Diagnostics.ApiKey),
                "Argus/Nightmare Diagnostics Enabled=true requires Diagnostics ApiKey.")
            .Validate(
                o => !o.DataMaintenance.Enabled || !string.IsNullOrWhiteSpace(o.DataMaintenance.ApiKey),
                "Argus/Nightmare DataMaintenance Enabled=true requires DataMaintenance ApiKey.")
            .ValidateOnStart();

        return services;
    }
}
