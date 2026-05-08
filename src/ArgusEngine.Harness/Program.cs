using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MassTransit;
using ArgusEngine.Infrastructure;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Application.Workers;
using ArgusEngine.Workers.Enumeration.Consumers;
using ArgusEngine.Contracts.Events;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Domain.Entities;
using ArgusEngine.Workers.Spider;
using Microsoft.EntityFrameworkCore;
using ArgusEngine.Harness.Core;
using ArgusEngine.Gatekeeper;
using ArgusEngine.Workers.Enumeration;
using ArgusEngine.Workers.HttpRequester;
using ArgusEngine.Workers.PortScan;
using ArgusEngine.Workers.HighValue;
using ArgusEngine.Workers.TechnologyIdentification;
using System.Net;

var rootCommand = new RootCommand("Argus Engine Worker Test Harness");

// COMMAND: run-enum
var runEnumCommand = new Command("run-enum", "Execute a subdomain enumeration provider locally.");
var targetIdOption = new Option<Guid>("--target-id", "The ID of the target to enumerate.") { IsRequired = true };
var providerOption = new Option<string>("--provider", () => "subfinder", "The provider to use (subfinder, amass).");
runEnumCommand.AddOption(targetIdOption);
runEnumCommand.AddOption(providerOption);

runEnumCommand.SetHandler(async (targetId, provider) =>
{
    var host = CreateHarnessHost();
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
    
    var target = await db.Targets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId);
    if (target == null)
    {
        Console.WriteLine($"Error: Target {targetId} not found.");
        return;
    }

    Console.WriteLine($"Executing {provider} enumeration for {target.RootDomain}...");
    var bus = scope.ServiceProvider.GetRequiredService<IBus>();
    await bus.Publish(new SubdomainEnumerationRequested(
        targetId,
        target.RootDomain,
        provider,
        "harness-manual",
        DateTimeOffset.UtcNow,
        CorrelationId: Guid.NewGuid(),
        EventId: Guid.NewGuid(),
        CausationId: Guid.NewGuid(),
        Producer: "harness"));
    
    Console.WriteLine("Event published to in-memory bus. Running host to process...");
    await host.RunAsync();
}, targetIdOption, providerOption);

// COMMAND: run-spider
var runSpiderCommand = new Command("run-spider", "Run the spider worker locally to process the HTTP request queue.");
runSpiderCommand.SetHandler(async () =>
{
    var host = CreateHarnessHost();
    Console.WriteLine("Starting spider worker in harness mode...");
    await host.RunAsync();
});

// COMMAND: list-targets
var listTargetsCommand = new Command("list-targets", "List all targets in the database.");
listTargetsCommand.SetHandler(async () =>
{
    var host = CreateHarnessHost();
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
    var targets = await db.Targets.AsNoTracking().OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
    
    Console.WriteLine($"Found {targets.Count} targets:");
    foreach (var t in targets)
    {
        Console.WriteLine($"{t.Id} | {t.RootDomain} | Created: {t.CreatedAtUtc}");
    }
});

// COMMAND: test-harness
var testHarnessCommand = new Command("test-harness", "Run health checks for all worker components.");
testHarnessCommand.SetHandler(async () =>
{
    var host = CreateHarnessHost();
    using var scope = host.Services.CreateScope();
    var runner = ActivatorUtilities.CreateInstance<HarnessRunner>(scope.ServiceProvider);
    Console.WriteLine("Executing Worker Test Harness...");
    var result = await runner.RunAllAsync(default);
    Console.WriteLine($"\nResults ({result.WorkerResults.Count} workers):");
    foreach (var r in result.WorkerResults)
    {
        var status = r.Success ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] {r.WorkerName}: {r.Message}");
        if (!string.IsNullOrEmpty(r.Output))
        {
            Console.WriteLine($"      Log: {r.Output}");
        }
    }
});

rootCommand.AddCommand(runEnumCommand);
rootCommand.AddCommand(runSpiderCommand);
rootCommand.AddCommand(listTargetsCommand);
rootCommand.AddCommand(testHarnessCommand);

return await rootCommand.InvokeAsync(args);

IHost CreateHarnessHost()
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
    
    builder.Configuration["ASPNETCORE_ENVIRONMENT"] = "Development";
    builder.Configuration.AddJsonFile("appsettings.json", optional: true);
    builder.Configuration.AddEnvironmentVariables();

    builder.Services.AddArgusInfrastructure(builder.Configuration);
    builder.Services.AddScoped<GatekeeperOrchestrator>();
    builder.Services.AddScoped<HarnessRunner>();

    // Register Health Checks
    builder.Services.AddScoped<IWorkerHealthCheck, GatekeeperWorkerHealthCheck>();
    builder.Services.AddScoped<IWorkerHealthCheck, EnumWorkerHealthCheck>();
    builder.Services.AddScoped<IWorkerHealthCheck, SpiderWorkerHealthCheck>();
    builder.Services.AddScoped<IWorkerHealthCheck, PortScanWorkerHealthCheck>();
    builder.Services.AddScoped<IWorkerHealthCheck, HighValueWorkerHealthCheck>();
    builder.Services.AddScoped<IWorkerHealthCheck, TechIdWorkerHealthCheck>();

    builder.Services.AddMassTransit(x =>
    {
        x.UsingInMemory((context, cfg) =>
        {
            cfg.ConfigureEndpoints(context);
        });
    });

    // HTTP requester services used by the local queue-processing harness path.
    builder.Services.AddSingleton<AdaptiveConcurrencyController>();
    builder.Services.AddHostedService<HttpRequesterWorker>();
    builder.Services.Configure<HttpRequesterOptions>(builder.Configuration.GetSection("HttpRequester"));

    builder.Services.AddHttpClient("requester")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    // Worker options
    builder.Services.Configure<SubdomainEnumerationOptions>(builder.Configuration.GetSection("Argus:SubdomainEnumeration"));

    return builder.Build();
}

partial class Program { }
