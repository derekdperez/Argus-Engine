using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using ArgusEngine.Infrastructure.Messaging;

namespace ArgusEngine.Infrastructure.Health;

internal sealed class PostgresConnectionHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres dependency check failed.", ex);
        }
    }
}

internal sealed class RedisConnectionHealthCheck(IConnectionMultiplexer multiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!multiplexer.IsConnected)
            {
                return HealthCheckResult.Unhealthy("Redis multiplexer is not connected.");
            }

            var database = multiplexer.GetDatabase();
            await database.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis dependency check failed.", ex);
        }
    }
}

internal sealed class RabbitMqTcpHealthCheck(IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var rabbit = options.Value;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(rabbit.Host, rabbit.Port, cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ TCP dependency check failed.", ex);
        }
    }
}
