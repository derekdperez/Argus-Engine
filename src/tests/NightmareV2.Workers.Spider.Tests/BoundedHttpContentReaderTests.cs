using System.Net.Http.Headers;
using System.Text;
using NightmareV2.Workers.Spider;
using Xunit;

namespace NightmareV2.Workers.Spider.Tests;

public sealed class BoundedHttpContentReaderTests
{
    [Fact]
    public async Task ReadAsStringAsync_ReturnsEmptyWhenLimitIsZeroOrNegative()
    {
        using var content = new StringContent("abcdef");

        var result = await BoundedHttpContentReader.ReadAsStringAsync(content, 0, CancellationToken.None);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task ReadAsStringAsync_StopsAtConfiguredCharacterLimit()
    {
        using var content = new StringContent("abcdefghijklmnopqrstuvwxyz", Encoding.UTF8);

        var result = await BoundedHttpContentReader.ReadAsStringAsync(content, 5, CancellationToken.None);

        Assert.Equal("abcde", result);
    }

    [Fact]
    public async Task ReadAsStringAsync_UsesDeclaredCharsetWhenValid()
    {
        var bytes = Encoding.GetEncoding("iso-8859-1").GetBytes("café");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
        {
            CharSet = "iso-8859-1",
        };

        var result = await BoundedHttpContentReader.ReadAsStringAsync(content, 10, CancellationToken.None);

        Assert.Equal("café", result);
    }

    [Fact]
    public async Task ReadAsStringAsync_FallsBackToUtf8ForUnknownCharset()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
        {
            CharSet = "not-a-real-charset",
        };

        var result = await BoundedHttpContentReader.ReadAsStringAsync(content, 10, CancellationToken.None);

        Assert.Equal("hello", result);
    }
}
