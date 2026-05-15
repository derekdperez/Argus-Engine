using System.Collections.Concurrent;
using System.Net;

namespace ArgusEngine.Workers.HttpRequester;

public sealed class ProxyHttpClientProvider
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    internal HttpClient GetClient(ProxyServerConfiguration proxy, HttpRequesterOptions options)
    {
        return _clients.GetOrAdd(proxy.CacheKey, _ => CreateClient(proxy, options));
    }

    private static HttpClient CreateClient(ProxyServerConfiguration proxy, HttpRequesterOptions options)
    {
        var webProxy = new WebProxy(new Uri($"{proxy.Scheme}://{proxy.Host}:{proxy.Port}"));

        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = false,
            Proxy = webProxy,
            UseProxy = true
        };

        if (options.AllowInsecureSsl)
        {
#pragma warning disable CA5359
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#pragma warning restore CA5359
        }

        return new HttpClient(handler, disposeHandler: true);
    }
}
