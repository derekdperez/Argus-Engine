using System.Net.Http.Json;
using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.Web.Clients;

public class DiscoveryApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public async Task<IReadOnlyList<TargetSummary>> GetTargetsAsync(CancellationToken ct = default)
    {
        return await Client.GetFromJsonAsync<IReadOnlyList<TargetSummary>>("/api/targets", ct).ConfigureAwait(false) ?? Array.Empty<TargetSummary>();
    }
}
