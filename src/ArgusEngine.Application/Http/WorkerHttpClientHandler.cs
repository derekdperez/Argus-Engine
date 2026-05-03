using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ArgusEngine.Application.Http;

public class WafBlockedException : HttpRequestException
{
    public WafBlockedException(string message) : base(message) { }
}

public class WorkerHttpClientHandler : DelegatingHandler
{
    private int _consecutiveBlocks;

    private const int MaxAllowedBlocks = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (Interlocked.Increment(ref _consecutiveBlocks) >= MaxAllowedBlocks)
            {
                response.Dispose();
                throw new WafBlockedException("Circuit breaker tripped. WAF or Rate-limiting has blocked this worker.");
            }
        }
        else if (response.IsSuccessStatusCode)
        {
            Interlocked.Exchange(ref _consecutiveBlocks, 0);
        }

        return response;
    }
}
