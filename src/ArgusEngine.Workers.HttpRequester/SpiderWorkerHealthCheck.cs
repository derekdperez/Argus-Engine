using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Workers;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Workers.HttpRequester;

public partial class HttpRequesterWorkerHealthCheck : IWorkerHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpRequesterWorkerHealthCheck> _logger;

    public HttpRequesterWorkerHealthCheck(IHttpClientFactory httpClientFactory, ILogger<HttpRequesterWorkerHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string WorkerName => "HttpRequester";

    [LoggerMessage(Level = LogLevel.Information, Message = "Running HTTP requester health check...")]
    private partial void LogRunningHealthCheck();

    public async Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct)
    {
        LogRunningHealthCheck();
        
        try
        {
            var client = _httpClientFactory.CreateClient("requester");
            // Test connectivity to a known safe endpoint or just verify client creation
            var response = await client.GetAsync("https://www.google.com", HttpCompletionOption.ResponseHeadersRead, ct);
            
            return new WorkerHealthCheckResult(
                response.IsSuccessStatusCode, 
                $"HTTP requester connectivity test: {response.StatusCode}",
                $"Target: google.com, Status: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new WorkerHealthCheckResult(false, $"HTTP requester connectivity test failed: {ex.Message}");
        }
    }
}
