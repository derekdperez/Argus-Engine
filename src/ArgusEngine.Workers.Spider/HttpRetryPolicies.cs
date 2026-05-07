using System.Net;
using System.Net.Sockets;
using Polly;
using Polly.Extensions.Http;

namespace ArgusEngine.Workers.Spider;

public static class HttpRetryPolicies
{
    public static IAsyncPolicy<HttpResponseMessage> SpiderRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError(exception => !IsNameResolutionFailure(exception))
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * attempt));

    private static bool IsNameResolutionFailure(HttpRequestException exception) =>
        exception.InnerException is SocketException socketException
        && socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData;
}
