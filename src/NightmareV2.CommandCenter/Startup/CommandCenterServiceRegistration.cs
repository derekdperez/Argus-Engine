using MassTransit;
using Microsoft.AspNetCore.Components;
using NightmareV2.Application.Sagas;
using NightmareV2.CommandCenter.Realtime;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using Radzen;

namespace NightmareV2.CommandCenter.Startup;

public static class CommandCenterServiceRegistration
{
    public static IServiceCollection AddCommandCenterServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _ = environment;

        OpsSnapshotBuilder.RegisterHttpClient(services);

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddRadzenComponents();

        services.AddScoped(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
        });

        services.AddNightmareInfrastructure(configuration);
        services.AddSignalR();
        services.AddScoped<DiscoveryRealtimeClient>();
        services.AddNightmareRabbitMq(
            configuration,
            consumers =>
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
                        r.ConcurrencyMode = MassTransit.ConcurrencyMode.Pessimistic;
                        r.ExistingDbContext<NightmareDbContext>();
                    });
            });

        services.AddOptions<NightmareRuntimeOptions>()
            .Bind(configuration.GetSection("Nightmare"))
            .Validate(
                o => !o.Diagnostics.Enabled || !string.IsNullOrWhiteSpace(o.Diagnostics.ApiKey),
                "Nightmare:Diagnostics:Enabled=true requires Nightmare:Diagnostics:ApiKey.")
            .Validate(
                o => !o.DataMaintenance.Enabled || !string.IsNullOrWhiteSpace(o.DataMaintenance.ApiKey),
                "Nightmare:DataMaintenance:Enabled=true requires Nightmare:DataMaintenance:ApiKey.")
            .ValidateOnStart();

        return services;
    }
}
