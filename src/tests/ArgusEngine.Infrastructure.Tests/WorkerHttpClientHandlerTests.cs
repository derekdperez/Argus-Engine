using System.Net;
using ArgusEngine.Application.Http;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class WorkerHttpClientHandlerTests
{
    [Fact]
    public async Task SendAsync_AllowsFirstFourBlockResponsesAndTripsOnTheFifth()
    {
        var innerHandler = new SequenceHandler(
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.TooManyRequests);

        using var client = CreateClient(innerHandler);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.GetAsync("https://example.test/");
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var exception = await Assert.ThrowsAsync<WafBlockedException>(() =>
            client.GetAsync("https://example.test/"));

        Assert.Contains("Circuit breaker tripped", exception.Message, StringComparison.Ordinal);
        Assert.Equal(5, innerHandler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_ResetsTheBlockCounterAfterASuccessfulResponse()
    {
        var innerHandler = new SequenceHandler(
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.OK,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden);

        using var client = CreateClient(innerHandler);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var blockedResponse = await client.GetAsync("https://example.test/");
            Assert.Equal(HttpStatusCode.Forbidden, blockedResponse.StatusCode);
        }

        using var successfulResponse = await client.GetAsync("https://example.test/");
        Assert.Equal(HttpStatusCode.OK, successfulResponse.StatusCode);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var blockedResponse = await client.GetAsync("https://example.test/");
            Assert.Equal(HttpStatusCode.Forbidden, blockedResponse.StatusCode);
        }

        await Assert.ThrowsAsync<WafBlockedException>(() =>
            client.GetAsync("https://example.test/"));
    }

    [Fact]
    public async Task SendAsync_DoesNotTreatServerErrorsAsWafBlocks()
    {
        var innerHandler = new SequenceHandler(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError);

        using var client = CreateClient(innerHandler);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.GetAsync("https://example.test/");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.Equal(5, innerHandler.RequestCount);
    }

    private static HttpClient CreateClient(HttpMessageHandler innerHandler)
    {
        var handler = new WorkerHttpClientHandler
        {
            InnerHandler = innerHandler
        };

        return new HttpClient(handler);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public int RequestCount { get; private set; }

        public SequenceHandler(params HttpStatusCode[] statusCodes)
        {
            _responses = new Queue<HttpResponseMessage>(
                statusCodes.Select(code => new HttpResponseMessage(code)));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No HTTP responses remain in the test sequence.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
