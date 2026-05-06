using System.Collections.Concurrent;
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
    ILogger<PortScanRequestedConsumer> logger) : IConsumer<PortScanRequested>
{
    private static readonly Action<ILogger, string, int, Exception?> LogScanSummary =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1, nameof(LogScanSummary)),
            "Port scan completed for {Host}; open ports: {Count}");

    private static readonly int[] DefaultPorts =
    [
        21, 22, 25, 53, 80, 110, 143, 443, 445, 465, 587, 631, 993, 995,
        1433, 1521, 2375, 2376, 3000, 3306, 3389, 4000, 5000, 5432,
        5672, 6379, 8080, 8443, 9200, 27017
    ];

    private static readonly ConcurrentDictionary<string, int[]> ParsedPortsCache = new(StringComparer.Ordinal);

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
        var occurredAt = DateTimeOffset.UtcNow;
        var discovered = new List<AssetDiscovered>(open.Count);

        foreach (var port in open)
        {
            discovered.Add(
                new AssetDiscovered(
                    m.TargetId,
                    m.TargetRootDomain,
                    m.GlobalMaxDepth,
                    m.Depth + 1,
                    AssetKind.OpenPort,
                    $"{m.HostOrIp}:{port}/tcp",
                    WorkerKeys.PortScan,
                    occurredAt,
                    m.CorrelationId,
                    AssetAdmissionStage.Raw,
                    null,
                    $"Port scan found open TCP port {port} on host {m.HostOrIp}.",
                    ParentAssetId: m.AssetId,
                    RelationshipType: AssetRelationshipType.ServedBy,
                    IsPrimaryRelationship: false,
                    EventId: NewId.NextGuid(),
                    CausationId: causation,
                    Producer: "worker-portscan"));
        }

        await outbox.EnqueueBatchAsync(discovered, context.CancellationToken).ConfigureAwait(false);
    }

    private static int[] ParsePorts(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return DefaultPorts;

        return ParsedPortsCache.GetOrAdd(csv, static value =>
        {
            var parsed = ParsePortsCore(value.AsSpan());
            return parsed.Length > 0 ? parsed : DefaultPorts;
        });
    }

    private static int[] ParsePortsCore(ReadOnlySpan<char> csv)
    {
        var ports = new List<int>(DefaultPorts.Length);
        var start = 0;

        for (var i = 0; i <= csv.Length; i++)
        {
            if (i < csv.Length && csv[i] != ',')
                continue;

            var token = csv[start..i].Trim();

            if (int.TryParse(token, out var port) && port is > 0 and <= 65_535)
                ports.Add(port);

            start = i + 1;
        }

        if (ports.Count == 0)
            return [];

        ports.Sort();

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < ports.Count; readIndex++)
        {
            if (readIndex > 0 && ports[readIndex] == ports[readIndex - 1])
                continue;

            ports[writeIndex++] = ports[readIndex];
        }

        var result = new int[writeIndex];

        for (var i = 0; i < writeIndex; i++)
            result[i] = ports[i];

        return result;
    }
}
