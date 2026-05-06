using System.Collections.Concurrent;
using System.Net.Sockets;
using ArgusEngine.Application.Workers;

namespace ArgusEngine.Infrastructure.Workers;

public sealed class DefaultPortScanService : IPortScanService
{
    public async Task<IReadOnlyList<int>> ScanOpenTcpPortsAsync(
        string hostOrIp,
        IReadOnlyList<int> ports,
        TimeSpan perPortTimeout,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (ports.Count == 0)
            return [];

        var openPorts = new ConcurrentBag<int>();

        await Parallel.ForEachAsync(
            ports,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 256),
            },
            async (port, token) =>
            {
                if (await IsOpenAsync(hostOrIp, port, perPortTimeout, token).ConfigureAwait(false))
                    openPorts.Add(port);
            }).ConfigureAwait(false);

        if (openPorts.IsEmpty)
            return [];

        var sorted = openPorts.ToArray();
        Array.Sort(sorted);
        return sorted;
    }

    private static async Task<bool> IsOpenAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
