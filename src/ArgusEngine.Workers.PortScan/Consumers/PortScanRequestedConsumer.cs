using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ArgusEngine.Application.Events;
using ArgusEngine.Application.Workers;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.Workers.PortScan.Consumers;

public sealed class PortScanRequestedConsumer(
    IWorkerToggleReader toggles,
    IPortScanService portScan,
    IInboxDeduplicator inbox,
    IEventOutbox outbox,
    IConfiguration configuration,
    ILogger<PortScanRequestedConsumer> logger)
    : IConsumer<PortScanRequested>
{
    private static readonly Action<ILogger, string, int, Exception?> LogScanSummary =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1, nameof(LogScanSummary)),
            "Port scan completed for {Host}; open ports: {Count}");

    private static readonly int[] DefaultPorts =
    [
        21, 22, 25, 53, 80, 110, 143, 443, 445, 465, 587, 631, 993, 995, 1433, 1521, 2375, 2376, 3000, 3306, 3389, 4000, 5000, 5432, 5672, 6379, 8080, 8443, 9200, 27017
    ];

    public async Task Consume(ConsumeContext<PortScanRequested> context)
    {
        if (!await inbox.TryBeginProcessingAsync(context.Message, nameof(PortScanRequestedConsumer), context.CancellationToken).ConfigureAwait(false))
            return;

        if (!await toggles.IsWorkerEnabledAsync(WorkerKeys.PortScan, context.CancellationToken).ConfigureAwait(false))
            return;

        var m = context.Message;
        var ports = ParsePorts(configuration["PortScan:Ports"]);
        var timeoutMs = Math.Clamp(configuration.GetValue("PortScan:TimeoutMs", 700), 100, 5000);
        var maxConcurrency = Math.Clamp(configuration.GetValue("PortScan:MaxConcurrency", 32), 1, 256);
        var open = await portScan.ScanOpenTcpPortsAsync(
                m.HostOrIp,
                ports,
                TimeSpan.FromMilliseconds(timeoutMs),
                maxConcurrency,
                context.CancellationToken)
            .ConfigureAwait(false);

        LogScanSummary(logger, m.HostOrIp, open.Count, null);
        if (open.Count == 0)
            return;

        var causation = m.EventId == Guid.Empty ? m.CorrelationId : m.EventId;
        foreach (var port in open)
        {
            await outbox.EnqueueAsync(
                    new AssetDiscovered(
                        m.TargetId,
                        m.TargetRootDomain,
                        m.GlobalMaxDepth,
                        m.Depth + 1,
                        AssetKind.OpenPort,
                        $"{m.HostOrIp}:{port}/tcp",
                        WorkerKeys.PortScan,
                        DateTimeOffset.UtcNow,
                        m.CorrelationId,
                        AssetAdmissionStage.Raw,
                        null,
                        $"Port scan found open TCP port {port} on host {m.HostOrIp}.",
                        ParentAssetId: m.AssetId,
                        RelationshipType: AssetRelationshipType.ServedBy,
                        IsPrimaryRelationship: false,
                        EventId: NewId.NextGuid(),
                        CausationId: causation,
                        Producer: "worker-portscan"),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static int[] ParsePorts(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return DefaultPorts;
        var parsed = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n > 0 && n <= 65535)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
        return parsed.Length > 0 ? parsed : DefaultPorts;
    }
}
