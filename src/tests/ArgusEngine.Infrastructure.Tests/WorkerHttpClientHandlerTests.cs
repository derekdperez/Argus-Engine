using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ArgusEngine.Application.Http;
using Xunit;

namespace ArgusEngine.Infrastructure.Tests;

public sealed class WorkerHttpClientHandlerTests
{
    [Fact]
    public async Task SendAsync_DisposesBlockedResponseBeforeThrowingCircuitBreakerException()
    {
        var contents = new List<TrackingContent>();
        using var client = new HttpClient(
            new WorkerHttpClientHandler
            {
                InnerHandler = new StubHandler(() =>
                {
                    var content = new TrackingContent();
                    contents.Add(content);

                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = content
                    };
                })
            });

        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync("https://example.test/");
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        await Assert.ThrowsAsync<WafBlockedException>(() => client.GetAsync("https://example.test/"));

        Assert.True(contents[^1].Disposed);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory());
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
