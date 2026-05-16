using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;
using ArgusEngine.Application.Assets;
using ArgusEngine.Application.DataRetention;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.FileStore;
using ArgusEngine.Application.Gatekeeping;
using ArgusEngine.Application.HighValue;
using ArgusEngine.Application.Orchestration;
using ArgusEngine.Application.TechnologyIdentification;
using ArgusEngine.Application.Http;
using ArgusEngine.Application.Workers;
using ArgusEngine.Infrastructure.Caching;
using ArgusEngine.Infrastructure.Configuration;
using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.DataRetention;
using ArgusEngine.Infrastructure.FileStore;
using ArgusEngine.Infrastructure.Gatekeeping;
using ArgusEngine.Infrastructure.Health;
using ArgusEngine.Infrastructure.HighValue;
using ArgusEngine.Infrastructure.Messaging;
using ArgusEngine.Infrastructure.Orchestration;
using ArgusEngine.Infrastructure.Persistence;
using ArgusEngine.Infrastructure.TechnologyIdentification;
using ArgusEngine.Infrastructure.Http;
using ArgusEngine.Infrastructure.Workers;

namespace ArgusEngine.Infrastructure;

public static class DependencyInjection
{
    private const string DevelopmentPostgresConnectionString =
        "Host=localhost;Port=5432;Database=argus_engine;Username=argus;Password=argus";

    private const string DevelopmentRedisConnectionString = "localhost:6379";
    private static readonly string[] ReadyPostgresTags = ["ready", "postgres"];
    private static readonly string[] ReadyPostgresFilestoreTags = ["ready", "postgres", "filestore"];
    private static readonly string[] ReadyRedisTags = ["ready", "redis"];
    private static readonly string[] ReadyRabbitMqTags = ["ready", "rabbitmq"];

    public static IServiceCollection AddArgusWorkerHeartbeat(this IServiceCollection services, string workerKey)
    {
        services.AddSingleton(sp => new WorkerHeartbeatService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IDbContextFactory<ArgusDbContext>>(),
            workerKey,
            sp.GetRequiredService<ILogger<WorkerHeartbeatService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<WorkerHeartbeatService>());
        services.AddHostedService<CloudRunPortProbeService>();
        
        return services;
    }

    public static IServiceCollection AddArgusInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool enableOutboxDispatcher = true) =>
        services.AddNightmareInfrastructure(configuration, enableOutboxDispatcher);

    public static IServiceCollection AddNightmareInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool enableOutboxDispatcher = true)
    {
        var isDevelopment = IsDevelopment(configuration);

        var redisConn = ResolveConnectionString(
            configuration,
            "Redis",
            isDevelopment,
            DevelopmentRedisConnectionString);

        var pgConn = ResolveConnectionString(
            configuration,
            "Postgres",
            isDevelopment,
            DevelopmentPostgresConnectionString);

        var fileStoreConn = ResolveFileStoreConnectionString(configuration, pgConn, isDevelopment);

        pgConn = ApplyDefaultMaxPoolSize(pgConn, configuration.GetArgusValue("Postgres:MaxPoolSize", 8));
        fileStoreConn = ApplyDefaultMaxPoolSize(fileStoreConn, configuration.GetArgusValue("FileStore:MaxPoolSize", 4));
        var postgresCommandTimeoutSeconds = Math.Clamp(configuration.GetArgusValue("Postgres:CommandTimeoutSeconds", 45), 5, 600);
        var postgresRetryCount = Math.Clamp(configuration.GetArgusValue("Postgres:RetryCount", 0), 0, 10);
        var postgresRetryDelaySeconds = Math.Clamp(configuration.GetArgusValue("Postgres:RetryMaxDelaySeconds", 5), 1, 120);
        var fileStoreCommandTimeoutSeconds = Math.Clamp(configuration.GetArgusValue("FileStore:CommandTimeoutSeconds", 45), 5, 600);
        var fileStoreRetryCount = Math.Clamp(configuration.GetArgusValue("FileStore:RetryCount", 3), 0, 10);
        var fileStoreRetryDelaySeconds = Math.Clamp(configuration.GetArgusValue("FileStore:RetryMaxDelaySeconds", 5), 1, 120);

        services.AddHealthChecks()
            .AddCheck(
                "postgres",
                new PostgresConnectionHealthCheck(pgConn),
                failureStatus: HealthStatus.Unhealthy,
                tags: ReadyPostgresTags)
            .AddCheck(
                "file-store-postgres",
                new PostgresConnectionHealthCheck(fileStoreConn),
                failureStatus: HealthStatus.Unhealthy,
                tags: ReadyPostgresFilestoreTags)
            .AddCheck<RedisConnectionHealthCheck>(
                "redis",
                failureStatus: HealthStatus.Unhealthy,
                tags: ReadyRedisTags)
            .AddCheck<RabbitMqTcpHealthCheck>(
                "rabbitmq",
                failureStatus: HealthStatus.Unhealthy,
                tags: ReadyRabbitMqTags);

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConn);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton<DistributedScanLock>();
        services.AddTransient(sp => new BulkAssetImporter(pgConn));

        void ConfigureNpgsql(DbContextOptionsBuilder options) =>
            options.UseNpgsql(
                pgConn,
                npgsql =>
                {
                    npgsql.CommandTimeout(postgresCommandTimeoutSeconds);
                    if (postgresRetryCount > 0)
                    {
                        npgsql.EnableRetryOnFailure(
                            postgresRetryCount,
                            TimeSpan.FromSeconds(postgresRetryDelaySeconds),
                            errorCodesToAdd: null);
                    }
                });
        services.AddDbContextFactory<ArgusDbContext>(ConfigureNpgsql);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ArgusDbContext>>().CreateDbContext());

        void ConfigureFileStore(DbContextOptionsBuilder options) =>
            options.UseNpgsql(
                fileStoreConn,
                npgsql =>
                {
                    npgsql.CommandTimeout(fileStoreCommandTimeoutSeconds);
                    if (fileStoreRetryCount > 0)
                    {
                        npgsql.EnableRetryOnFailure(
                            fileStoreRetryCount,
                            TimeSpan.FromSeconds(fileStoreRetryDelaySeconds),
                            errorCodesToAdd: null);
                    }
                });
        services.AddDbContextFactory<FileStoreDbContext>(ConfigureFileStore);
        services.AddSingleton<IFileStore, EfFileStore>();
        services.AddOptions<HttpArtifactOptions>()
            .Bind(configuration.GetArgusSection("HttpArtifacts"))
            .Validate(o => o.MaxStoredResponseBodyBytes is >= 1024 and <= 50_000_000)
            .Validate(o => o.MaxPreviewChars is >= 0 and <= 32_768)
            .ValidateOnStart();

        services.AddScoped<IHttpArtifactStore, EfHttpArtifactStore>();
        services.AddScoped<IHttpArtifactReader, EfHttpArtifactStore>();
        services.AddSingleton<IAssetCanonicalizer, DefaultAssetCanonicalizer>();
        services.AddSingleton<IAssetDeduplicator, RedisAssetDeduplicator>();
        services.AddSingleton<ITargetScopeEvaluator, DnsTargetScopeEvaluator>();
        services.AddScoped<IAssetAdmissionDecisionWriter, EfAssetAdmissionDecisionWriter>();
        services.AddHostedService<AssetAdmissionDecisionSchemaInitializer>();
        services.AddHostedService<HttpRequestQueueArtifactSchemaInitializer>();
        services.AddScoped<IAssetRelationshipValidator, EfAssetRelationshipValidator>();
        services.AddScoped<IAssetGraphService, EfAssetGraphService>();
        services.AddScoped<IAssetPersistence, EfAssetPersistence>();
        services.AddOptions<HighValueScanOptions>()
            .Bind(configuration.GetArgusSection("HighValueScan"))
            .Validate(o => o.MaxResponseBodyScanBytes is >= 1024 and <= 10_000_000)
            .ValidateOnStart();
        services.AddOptions<TechnologyIdentificationScanOptions>()
            .Bind(configuration.GetArgusSection("TechnologyIdentificationScan"))
            .Validate(o => o.MaxResponseBodyScanBytes is >= 1024 and <= 10_000_000)
            .ValidateOnStart();
        services.AddOptions<ReconOrchestratorOptions>()
            .Bind(configuration.GetArgusSection("ReconOrchestrator"))
            .Validate(o => o.MaxTargetsPerTick is >= 1 and <= 10_000)
            .Validate(o => o.MaxSubdomainsPerTick is >= 1 and <= 100_000)
            .Validate(o => o.ProviderRunTimeoutSeconds is >= 60 and <= 86_400)
            .Validate(o => o.RequestedRunRetryDelaySeconds is >= 15 and <= 86_400)
            .Validate(o => o.MaxRequestedRunRetries is >= 0 and <= 100)
            .Validate(o => o.MaxHttpWorkersPerSubdomain is >= 1 and <= 128)
            .Validate(o => o.RequestsPerSecondPerWorker is >= 1 and <= 1_000)
            .Validate(o => o.MaxConcurrentSubdomainsPerWorker is >= 1 and <= 1_000)
            .Validate(o => o.ReconProfilesPerTarget is >= 1 and <= 128)
            .Validate(o => o.ReconProfilesPerSubdomain is >= 1 and <= 64)
            .Validate(o => o.RequestsPerMinutePerSubdomain is >= 1 and <= 60_000)
            .Validate(o => o.RandomDelayMin >= 0)
            .Validate(o => o.RandomDelayMax >= o.RandomDelayMin)
            .Validate(o => o.ReconProfileHardwareAge is >= 0 and <= 25)
            .ValidateOnStart();
        services.AddHostedService<ReconOrchestratorSchemaInitializer>();
        services.AddScoped<IReconOrchestrator, EfReconOrchestrator>();
        services.AddScoped<IReconProviderRunRecorder, EfReconProviderRunRecorder>();
        services.AddScoped<IReconProfileAssignmentService, EfReconProfileAssignmentService>();
        services.AddScoped<IHighValueFindingWriter, EfHighValueFindingWriter>();
        services.AddScoped<IAssetTagService, EfAssetTagService>();
        services.AddScoped<IWorkerToggleReader, EfWorkerToggleReader>();
        services.AddScoped<ITargetLookup, EfTargetLookup>();
        services.AddSingleton<IHttpRequestQueueStateMachine, DefaultHttpRequestQueueStateMachine>();
        services.AddOptions<SubdomainEnumerationOptions>().Bind(configuration.GetSection("SubdomainEnumeration"));
        services.AddOptions<HttpRateLimitOptions>().Bind(configuration.GetArgusSection("HttpRateLimit"));
        services.AddSingleton<IHttpRateLimiter, InMemoryHttpRateLimiter>();
        services.AddSingleton<ToolProcessRunner>();
        services.AddSingleton<IHostResolver, DefaultHostResolver>();
        services.AddSingleton<ISubdomainEnumerationProvider, SubfinderEnumerationProvider>();
        services.AddSingleton<ISubdomainEnumerationProvider, AmassEnumerationProvider>();
        services.AddSingleton<IPortScanService, DefaultPortScanService>();
        services.AddScoped<IEventOutbox, EfEventOutbox>();
        services.AddScoped<IInboxDeduplicator, EfInboxDeduplicator>();
        services.AddSingleton<WorkerCancellationTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkerCancellationTracker>());
        if (enableOutboxDispatcher)
        {
            services.AddHostedService<OutboxDispatcherWorker>();
        }

        services.AddSingleton<BusJournalBuffer>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BusJournalBuffer>());
        services.AddSingleton<BusJournalPublishObserver>();
        services.AddSingleton<BusJournalConsumeObserver>();
        services.AddOptions<DataRetentionOptions>()
            .Bind(configuration.GetArgusSection("DataRetention"))
            .Validate(o => o.RunIntervalMinutes is >= 1 and <= 1440)
            .Validate(o => o.BatchSize is >= 100 and <= 10_000)
            .Validate(o => o.DelayBetweenBatchesMs is >= 0 and <= 10_000)
            .Validate(o => o.MaxBatchesPerRun is >= 1 and <= 10_000)
            .Validate(o => o.SucceededOutboxRetentionHours is >= 0 and <= 8760)
            .Validate(o => o.FailedOutboxRetentionHours is >= 0 and <= 8760)
            .Validate(o => o.DeadLetterOutboxRetentionHours is >= 0 and <= 8760)
            .Validate(o => o.InboxRetentionHours is >= 0 and <= 8760)
            .Validate(o => o.BusJournalRetentionHours is >= 0 and <= 8760)
            .ValidateOnStart();
        services.AddSingleton<DataRetentionRunState>();
        services.AddSingleton<DataRetentionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<DataRetentionWorker>());
        services.AddScoped<IPartitionMaintenanceService, PostgresPartitionMaintenanceService>();
        services.AddHostedService<PostgresPartitionMaintenanceHostedService>();

        return services;
    }

    private static string ResolveConnectionString(
        IConfiguration configuration,
        string name,
        bool isDevelopment,
        string developmentFallback)
    {
        var configured = configuration.GetConnectionString(name);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (isDevelopment)
        {
            return developmentFallback;
        }

        throw new InvalidOperationException(
            $"Missing required connection string 'ConnectionStrings:{name}'. Local development defaults are disabled outside Development.");
    }

    private static string ResolveFileStoreConnectionString(
        IConfiguration configuration,
        string postgresConnectionString,
        bool isDevelopment)
    {
        var configured = configuration.GetConnectionString("FileStore");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "Missing required connection string 'ConnectionStrings:FileStore'. The file-store database must be configured explicitly outside Development.");
        }

        var builder = new NpgsqlConnectionStringBuilder(postgresConnectionString)
        {
            Database = "argus_engine_files",
        };

        return builder.ConnectionString;
    }

    private static bool IsDevelopment(IConfiguration configuration)
    {
        var environmentName =
            configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"]
            ?? Environments.Production;

        return string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyDefaultMaxPoolSize(string connectionString, int maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (!builder.ContainsKey("Maximum Pool Size") &&
            !builder.ContainsKey("MaxPoolSize") &&
            !builder.ContainsKey("Max Pool Size"))
        {
            builder.MaxPoolSize = maxPoolSize;
        }

        return builder.ConnectionString;
    }
}
