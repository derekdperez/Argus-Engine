using ArgusEngine.Infrastructure.Data;
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
        var redis = ResolveConnectionString(configuration, "Redis", "localhost:6379");

        services.AddDbContextFactory<ArgusDbContext>(options => options.UseNpgsql(postgres));
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
