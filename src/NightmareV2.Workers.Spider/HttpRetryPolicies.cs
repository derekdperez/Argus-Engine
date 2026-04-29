using System.Net;
using Polly;
using Polly.Extensions.Http;

namespace NightmareV2.Workers.Spider;

public static class HttpRetryPolicies
{
    public static IAsyncPolicy<HttpResponseMessage> SpiderRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * attempt));
}
