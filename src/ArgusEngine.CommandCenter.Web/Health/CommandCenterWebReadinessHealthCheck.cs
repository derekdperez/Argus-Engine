using ArgusEngine.CommandCenter.Web.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CommandCenter.Web.Health;

public sealed class CommandCenterWebReadinessHealthCheck(
    IOptions<CommandCenterWebOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var gatewayBaseUrl = options.Value.GatewayBaseUrl;

        if (string.IsNullOrWhiteSpace(gatewayBaseUrl))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "CommandCenter:GatewayBaseUrl is not configured."));
        }

        if (!Uri.TryCreate(gatewayBaseUrl, UriKind.Absolute, out var gatewayUri))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"CommandCenter:GatewayBaseUrl is not an absolute URI: '{gatewayBaseUrl}'."));
        }

        var data = new Dictionary<string, object>
        {
            ["gatewayBaseUrl"] = gatewayUri.ToString()
        };

        return Task.FromResult(HealthCheckResult.Healthy(
            "Command Center Web has valid gateway routing configuration.",
            data: data));
    }
}
