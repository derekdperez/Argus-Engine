using System.Text.Json;
using ArgusEngine.Infrastructure.Configuration;

namespace ArgusEngine.CommandCenter.WorkerControl.Api.Services;

public sealed class AwsRegionResolver(IConfiguration configuration)
{
    public async Task<string?> ResolveAsync(CancellationToken ct)
    {
        var configured = configuration.GetArgusValue("Aws:Region")
            ?? configuration["AWS_REGION"]
            ?? configuration["AWS_DEFAULT_REGION"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var token = "";
            using (var tokenRequest = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token"))
            {
                tokenRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "60");
                using var tokenResponse = await http.SendAsync(tokenRequest, ct).ConfigureAwait(false);
                if (tokenResponse.IsSuccessStatusCode)
                    token = await tokenResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            using var identityRequest = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/dynamic/instance-identity/document");
            if (!string.IsNullOrWhiteSpace(token))
                identityRequest.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);

            using var identityResponse = await http.SendAsync(identityRequest, ct).ConfigureAwait(false);
            if (!identityResponse.IsSuccessStatusCode)
                return null;

            var json = await identityResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("region", out var region) ? region.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}

