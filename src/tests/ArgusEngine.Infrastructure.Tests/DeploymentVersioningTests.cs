using System.Net;
using ArgusEngine.Application.Http;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class DeploymentVersioningTests
{
    [Fact]
    public async Task WafCircuitBreakerDisposesTheResponseThatTripsTheCircuit()
    {
        var content = new TrackingContent();
        var innerHandler = new FixedResponseHandler(
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content });

        using var client = new HttpClient(new WorkerHttpClientHandler { InnerHandler = innerHandler });

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.GetAsync("https://example.test/");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await Assert.ThrowsAsync<WafBlockedException>(() => client.GetAsync("https://example.test/"));

        Assert.True(content.WasDisposed);
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public FixedResponseHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool WasDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
