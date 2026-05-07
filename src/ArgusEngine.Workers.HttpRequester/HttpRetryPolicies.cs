using System.Net;
using System.Net.Sockets;
using Polly;

namespace ArgusEngine.Workers.Spider;

public static class HttpRetryPolicies
{
    public static IAsyncPolicy<HttpResponseMessage> SpiderRetryPolicy() =>
        Policy<HttpResponseMessage>
            .Handle<HttpRequestException>(exception => !IsNameResolutionFailure(exception))
            .Or<TaskCanceledException>()
            .OrResult(response => response.StatusCode == HttpStatusCode.RequestTimeout)
            .OrResult(response => (int)response.StatusCode >= 500)
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * attempt));

    private static bool IsNameResolutionFailure(HttpRequestException exception) =>
        exception.InnerException is SocketException socketException
        && socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData;
}
