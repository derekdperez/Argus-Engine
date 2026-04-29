using System.Net;
using NightmareV2.Application.Workers;

namespace NightmareV2.Infrastructure.Workers;

public sealed class DefaultHostResolver : IHostResolver
{
    public async Task<IReadOnlyCollection<string>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default)
    {
        var addrs = await Dns.GetHostAddressesAsync(hostname, cancellationToken).ConfigureAwait(false);
        return addrs
            .Select(a => a.ToString())
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
    }
}
