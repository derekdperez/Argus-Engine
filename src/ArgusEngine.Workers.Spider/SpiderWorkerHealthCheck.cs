using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.Spider;

public class SpiderWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpiderWorkerHealthCheck> _logger;

    public SpiderWorkerHealthCheck(IHttpClientFactory httpClientFactory, ILogger<SpiderWorkerHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string WorkerName => "Spider";

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Running Spider health check...");
        
        try
        {
            var client = _httpClientFactory.CreateClient("spider");
            // Test connectivity to a known safe endpoint or just verify client creation
            var response = await client.GetAsync("https://www.google.com", HttpCompletionOption.ResponseHeadersRead, ct);
            
            return new WorkerHealthCheckResult(
                response.IsSuccessStatusCode, 
                $"Spider connectivity test: {response.StatusCode}",
                $"Target: google.com, Status: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new WorkerHealthCheckResult(false, $"Spider connectivity test failed: {ex.Message}");
        }
    }
}
