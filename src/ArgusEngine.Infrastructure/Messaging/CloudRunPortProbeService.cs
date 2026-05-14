using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusEngine.Infrastructure.Messaging;

/// <summary>
/// Cloud Run services require a process that listens on PORT. Argus workers are
/// background hosts, so this lightweight probe listener keeps the container
/// revision healthy without changing worker execution flow.
/// </summary>
public sealed partial class CloudRunPortProbeService(
    ILogger<CloudRunPortProbeService> logger) : BackgroundService
{
    private const int DefaultProbePort = 8080;
    private static readonly byte[] HttpOkResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "Content-Length: 2\r\n" +
        "Connection: close\r\n\r\n" +
        "OK");

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Cloud Run probe listener started on 0.0.0.0:{Port}.")]
    private partial void LogProbeStarted(int port);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Cloud Run probe listener disabled because no Cloud Run runtime markers were found.")]
    private partial void LogProbeDisabled();

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Cloud Run probe listener failed on port {Port}.")]
    private partial void LogProbeFailed(int port, Exception ex);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Cloud Run probe listener accepted request from {RemoteEndPoint}.")]
    private partial void LogProbeRequest(string remoteEndPoint);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldStart())
        {
            LogProbeDisabled();
            return;
        }

        var port = ResolvePort();
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
            LogProbeStarted(port);

            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (Exception ex)
        {
            LogProbeFailed(port, ex);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;

        try
        {
            if (client.Client.RemoteEndPoint is { } remote)
            {
                LogProbeRequest(remote.ToString());
            }

            using var stream = client.GetStream();

            // Drain up to one small request buffer (best effort) so TCP clients
            // speaking HTTP don't see a hard close before request bytes are sent.
            var readBuffer = new byte[1024];
            if (stream.DataAvailable)
            {
                _ = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct).ConfigureAwait(false);
            }

            await stream.WriteAsync(HttpOkResponse.AsMemory(0, HttpOkResponse.Length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Probe handling should never crash the worker host.
        }
    }

    private static int ResolvePort()
    {
        var raw = Environment.GetEnvironmentVariable("PORT");
        return int.TryParse(raw, out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultProbePort;
    }

    private static bool ShouldStart()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("K_SERVICE"))
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLOUD_RUN_WORKER_POOL"))
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PORT"));
    }
}
