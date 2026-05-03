using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using StackExchange.Redis;

using NightmareV2.Application.Assets;
using NightmareV2.Application.DataRetention;
using NightmareV2.Application.Events;
using NightmareV2.Application.FileStore;
using NightmareV2.Application.Gatekeeping;
using NightmareV2.Application.HighValue;
using NightmareV2.Application.TechnologyIdentification;
using NightmareV2.Application.Workers;
using NightmareV2.Infrastructure.Caching;
using NightmareV2.Infrastructure.Configuration;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.DataRetention;
using NightmareV2.Infrastructure.FileStore;
using NightmareV2.Infrastructure.Gatekeeping;
using NightmareV2.Infrastructure.HighValue;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Infrastructure.Persistence;
using NightmareV2.Infrastructure.TechnologyIdentification;
using NightmareV2.Infrastructure.Workers;

namespace NightmareV2.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddArgusInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
        services.AddNightmareInfrastructure(configuration);

    public static IServiceCollection AddNightmareInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConn = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        var pgConn = configuration.GetConnectionString("Postgres")
                     ?? "Host=localhost;Port=5432;Database=nightmare_v2;Username=nightmare;Password=nightmare";
        var fileStoreConn = configuration.GetConnectionString("FileStore")
                            ?? configuration.GetConnectionString("Postgres")?.Replace(
                                "Database=nightmare_v2",
                                "Database=nightmare_v2_files",
                                StringComparison.OrdinalIgnoreCase)
                            ?? "Host=localhost;Port=5432;Database=nightmare_v2_files;Username=nightmare;Password=nightmare";

        pgConn = ApplyDefaultMaxPoolSize(pgConn, configuration.GetArgusValue("Postgres:MaxPoolSize", 8));
        fileStoreConn = ApplyDefaultMaxPoolSize(fileStoreConn, configuration.GetArgusValue("FileStore:MaxPoolSize", 4));

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
        services.AddSingleton<DistributedScanLock>();
        services.AddTransient(sp => new BulkAssetImporter(pgConn));

        void ConfigureNpgsql(DbContextOptionsBuilder options) => options.UseNpgsql(pgConn);

        services.AddDbContext<NightmareDbContext>(ConfigureNpgsql);
        services.AddDbContextFactory<NightmareDbContext>(ConfigureNpgsql);

        void ConfigureFileStore(DbContextOptionsBuilder options) => options.UseNpgsql(fileStoreConn);

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

        services.AddScoped<IHighValueFindingWriter, EfHighValueFindingWriter>();
        services.AddScoped<IAssetTagService, EfAssetTagService>();
        services.AddScoped<IWorkerToggleReader, EfWorkerToggleReader>();
        services.AddScoped<ITargetLookup, EfTargetLookup>();

        services.AddSingleton<IHttpRequestQueueStateMachine, DefaultHttpRequestQueueStateMachine>();

        services.AddOptions<SubdomainEnumerationOptions>().Bind(configuration.GetSection("SubdomainEnumeration"));
        services.AddSingleton<ToolProcessRunner>();
        services.AddSingleton<IHostResolver, DefaultHostResolver>();
        services.AddSingleton<ISubdomainEnumerationProvider, SubfinderEnumerationProvider>();
        services.AddSingleton<ISubdomainEnumerationProvider, AmassEnumerationProvider>();
        services.AddSingleton<IPortScanService, DefaultPortScanService>();

        services.AddScoped<IEventOutbox, EfEventOutbox>();
        services.AddScoped<IInboxDeduplicator, EfInboxDeduplicator>();
        services.AddHostedService<OutboxDispatcherWorker>();

        services.AddSingleton<BusJournalBuffer>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BusJournalBuffer>());
        services.AddSingleton<BusJournalPublishObserver>();
        services.AddSingleton<BusJournalConsumeObserver>();

        services.AddOptions<DataRetentionOptions>()
            .Bind(configuration.GetArgusSection("DataRetention"))
            .Validate(o => o.BatchSize is >= 100 and <= 10_000)
            .Validate(o => o.DelayBetweenBatchesMs is >= 0 and <= 10_000)
            .Validate(o => o.MaxBatchesPerRun is >= 1 and <= 10_000)
            .ValidateOnStart();

        services.AddSingleton<DataRetentionRunState>();
        services.AddSingleton<DataRetentionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<DataRetentionWorker>());
        services.AddScoped<IPartitionMaintenanceService, PostgresPartitionMaintenanceService>();
        services.AddHostedService<PostgresPartitionMaintenanceHostedService>();

        return services;
    }

    private static string ApplyDefaultMaxPoolSize(string connectionString, int maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ContainsKey("Maximum Pool Size")
            && !builder.ContainsKey("MaxPoolSize")
            && !builder.ContainsKey("Max Pool Size"))
        {
            builder.MaxPoolSize = maxPoolSize;
        }

        return builder.ConnectionString;
    }
}
