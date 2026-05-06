using MassTransit;

using Microsoft.AspNetCore.Components;

using ArgusEngine.CommandCenter.DataMaintenance;

using ArgusEngine.Application.Sagas;
using ArgusEngine.Application.Gatekeeping;

using ArgusEngine.Harness.Core;

using ArgusEngine.Application.Workers;

using ArgusEngine.CommandCenter.Realtime;

using ArgusEngine.CommandCenter.Services;

using ArgusEngine.CommandCenter.Services.Aws;

using ArgusEngine.CommandCenter.Services.Status;

using ArgusEngine.CommandCenter.Services.Targets;

using ArgusEngine.CommandCenter.Services.Updates;

using ArgusEngine.CommandCenter.Services.Workers;

using ArgusEngine.Workers.Enumeration.Consumers;

using ArgusEngine.Workers.Spider;

using ArgusEngine.Gatekeeper;
using ArgusEngine.Workers.Enumeration;

using ArgusEngine.Workers.PortScan;

using ArgusEngine.Workers.HighValue;

using ArgusEngine.Workers.TechnologyIdentification;

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

 services.AddHttpClient("ops-rabbit");

 services.AddHttpClient("spider");

 services.AddScoped(sp =>

 {
 var nav = sp.GetRequiredService<NavigationManager>();

 return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };

 });

 services.AddArgusInfrastructure(configuration);

 services.AddSignalR();

 services.AddScoped<DiscoveryRealtimeClient>();

 services.AddScoped<IRealtimeUpdatePublisher, SignalRRealtimeUpdatePublisher>();

 services.AddCommandCenterApplicationServices();

 services.AddCommandCenterOptions(configuration);

 services.AddComponentUpdateServices(configuration);

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

 r.ExistingDbContext<ArgusDbContext>();

 });

 });

 return services;

 }

 private static IServiceCollection AddCommandCenterApplicationServices(this IServiceCollection services)

 {

 services.AddScoped<RootSpiderSeedService>();

 services.AddScoped<HttpQueueArtifactBackfillService>();

 services.AddScoped<ICommandCenterStatusSnapshotService, CommandCenterStatusSnapshotService>();

 services.AddSingleton<WorkerScaleDefinitionProvider>();
 services.AddScoped<AwsRegionResolver>();

 services.AddScoped<EcsWorkerServiceManager>();

 services.AddScoped<EcsServiceNameResolver>();

 services.AddScoped<HarnessRunner>();

 // Register Worker Health Checks for the Harness

 services.AddScoped<IWorkerHealthCheck, GatekeeperWorkerHealthCheck>();

 services.AddScoped<IWorkerHealthCheck, EnumWorkerHealthCheck>();

 services.AddScoped<IWorkerHealthCheck, SpiderWorkerHealthCheck>();

 services.AddScoped<IWorkerHealthCheck, PortScanWorkerHealthCheck>();
 services.AddScoped<IWorkerHealthCheck, HighValueWorkerHealthCheck>();

 services.AddScoped<IWorkerHealthCheck, TechIdWorkerHealthCheck>();

 services.AddScoped<GatekeeperOrchestrator>();

 return services;

 }

 private static IServiceCollection AddCommandCenterOptions(this IServiceCollection services, IConfiguration configuration)

 {

 services.AddOptions<ArgusRuntimeOptions>()

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
 // Bind worker options to their specific sections as defined in docker-compose/env vars

 services.Configure<SubdomainEnumerationOptions>(configuration.GetSection("Enumeration"));

 services.Configure<SpiderHttpOptions>(configuration.GetSection("Spider"));

 return services;

 }

}
