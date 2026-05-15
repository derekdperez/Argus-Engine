using ArgusEngine.Infrastructure.Data;
using ArgusEngine.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;

namespace ArgusEngine.CommandCenter.Operations.Api;

internal static class OperationsApiServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsApi(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = ApplyDefaultMaxPoolSize(
            ResolveConnectionString(configuration, "Postgres", "Host=localhost;Port=5432;Database=argus_engine;Username=argus;Password=argus"),
            configuration["Argus:Postgres:MaxPoolSize"] ?? configuration["Nightmare:Postgres:MaxPoolSize"] ?? "8");
        var fileStore = ApplyDefaultMaxPoolSize(
            ResolveConnectionString(configuration, "FileStore", postgres),
            configuration["Argus:FileStore:MaxPoolSize"] ?? configuration["Nightmare:FileStore:MaxPoolSize"] ?? "4");
        var redis = ResolveConnectionString(configuration, "Redis", "localhost:6379");
        var postgresCommandTimeoutSeconds = Math.Clamp(configuration.GetArgusValue("Postgres:CommandTimeoutSeconds", 45), 5, 600);
        var postgresRetryCount = Math.Clamp(configuration.GetArgusValue("Postgres:RetryCount", 3), 0, 10);
        var postgresRetryDelaySeconds = Math.Clamp(configuration.GetArgusValue("Postgres:RetryMaxDelaySeconds", 5), 1, 120);
        var fileStoreCommandTimeoutSeconds = Math.Clamp(configuration.GetArgusValue("FileStore:CommandTimeoutSeconds", 45), 5, 600);
        var fileStoreRetryCount = Math.Clamp(configuration.GetArgusValue("FileStore:RetryCount", 3), 0, 10);
        var fileStoreRetryDelaySeconds = Math.Clamp(configuration.GetArgusValue("FileStore:RetryMaxDelaySeconds", 5), 1, 120);

        services.AddDbContextFactory<ArgusDbContext>(
            options => options.UseNpgsql(
                postgres,
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
                }));
        services.AddDbContextFactory<FileStoreDbContext>(
            options => options.UseNpgsql(
                fileStore,
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
                }));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ArgusDbContext>>().CreateDbContext());
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddHttpClient(OperationsSnapshotBuilder.RabbitHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(12));
        services.AddScoped<OperationsStatusService>();

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string name, string developmentFallback) =>
        configuration.GetConnectionString(name) is { Length: > 0 } configured ? configured : developmentFallback;

    private static string ApplyDefaultMaxPoolSize(string connectionString, string configuredMaxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ContainsKey("MaxPoolSize") && int.TryParse(configuredMaxPoolSize, out var maxPoolSize) && maxPoolSize > 0)
        {
            builder.MaxPoolSize = maxPoolSize;
        }

        return builder.ConnectionString;
    }
}
