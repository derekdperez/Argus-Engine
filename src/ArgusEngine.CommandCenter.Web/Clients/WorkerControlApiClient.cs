using System.Net.Http.Json;
using ArgusEngine.CommandCenter.Contracts;

namespace ArgusEngine.CommandCenter.Web.Clients;

public class WorkerControlApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public async Task<DockerWorkerStatusSnapshotDto> GetDockerWorkerStatusAsync(CancellationToken ct = default)
    {
        var snapshot = await Client
            .GetFromJsonAsync<DockerWorkerStatusSnapshotDto>("/api/workers/docker-status", ct)
            .ConfigureAwait(false);

        return snapshot ?? new DockerWorkerStatusSnapshotDto(
            DateTimeOffset.UtcNow,
            DockerAvailable: false,
            Error: "Worker Control API returned an empty Docker worker status response.",
            Services: Array.Empty<DockerWorkerServiceDto>());
    }

    public async Task<DockerWorkerScaleResult> ScaleDockerWorkerAsync(
        string serviceName,
        int desiredCount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Worker service name is required.", nameof(serviceName));
        }

        if (desiredCount < 0 || desiredCount > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredCount), desiredCount, "Desired count must be between 0 and 50.");
        }

        var request = new DockerWorkerScaleRequest(desiredCount);
        using var response = await Client
            .PutAsJsonAsync($"/api/workers/{Uri.EscapeDataString(serviceName)}/docker-scale", request, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Docker worker scale request failed for {serviceName} ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
        }

        var result = await response.Content
            .ReadFromJsonAsync<DockerWorkerScaleResult>(cancellationToken: ct)
            .ConfigureAwait(false);

        return result ?? throw new InvalidOperationException(
            $"Worker Control API returned an empty scale response for {serviceName}.");
    }
}
