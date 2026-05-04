using MassTransit;
using Microsoft.AspNetCore.SignalR;
using ArgusEngine.CommandCenter.Hubs;
using ArgusEngine.CommandCenter.Models;
using ArgusEngine.Contracts;
using ArgusEngine.Contracts.Events;

namespace ArgusEngine.CommandCenter.Realtime;

internal static class CommandCenterUiEventPublisher
{
    public static Task PublishAsync(
        IHubContext<DiscoveryHub> hub,
        LiveUiEventDto evt,
        CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync(DiscoveryHubEvents.DomainEvent, evt, cancellationToken);
}

public sealed class TargetCreatedUiEventConsumer(IHubContext<DiscoveryHub> hub) : IConsumer<TargetCreated>
{
    public Task Consume(ConsumeContext<TargetCreated> context)
    {
        var m = context.Message;
        return CommandCenterUiEventPublisher.PublishAsync(
            hub,
            new LiveUiEventDto(
                "TargetCreated",
                m.TargetId,
                m.TargetId,
                "targets",
                $"Target queued: {m.RootDomain}",
                m.OccurredAtUtc),
            context.CancellationToken);
    }
}

public sealed class AssetDiscoveredUiEventConsumer(IHubContext<DiscoveryHub> hub) : IConsumer<AssetDiscovered>
{
    public Task Consume(ConsumeContext<AssetDiscovered> context)
    {
        var m = context.Message;
        var kind = m.AdmissionStage == AssetAdmissionStage.Indexed ? "AssetIndexed" : "AssetDiscovered";
        return CommandCenterUiEventPublisher.PublishAsync(
            hub,
            new LiveUiEventDto(
                kind,
                m.TargetId,
                m.AssetId,
                "assets",
                $"{m.Kind} discovered: {m.RawValue}",
                m.OccurredAtUtc),
            context.CancellationToken);
    }
}

public sealed class ScannableContentAvailableUiEventConsumer(IHubContext<DiscoveryHub> hub) : IConsumer<ScannableContentAvailable>
{
    public Task Consume(ConsumeContext<ScannableContentAvailable> context)
    {
        var m = context.Message;
        return CommandCenterUiEventPublisher.PublishAsync(
            hub,
            new LiveUiEventDto(
                "ScannableContentAvailable",
                m.TargetId,
                m.AssetId,
                "http",
                $"HTTP content stored: {m.SourceUrl}",
                m.OccurredAtUtc),
            context.CancellationToken);
    }
}

public sealed class CriticalHighValueFindingAlertUiEventConsumer(IHubContext<DiscoveryHub> hub) : IConsumer<CriticalHighValueFindingAlert>
{
    public Task Consume(ConsumeContext<CriticalHighValueFindingAlert> context)
    {
        var m = context.Message;
        return CommandCenterUiEventPublisher.PublishAsync(
            hub,
            new LiveUiEventDto(
                "CriticalHighValueFinding",
                m.TargetId,
                m.FindingId,
                "findings",
                $"Critical finding: {m.PatternName}",
                m.OccurredAtUtc),
            context.CancellationToken);
    }
}

public sealed class PortScanRequestedUiEventConsumer(IHubContext<DiscoveryHub> hub) : IConsumer<PortScanRequested>
{
    public Task Consume(ConsumeContext<PortScanRequested> context)
    {
        var m = context.Message;
        return CommandCenterUiEventPublisher.PublishAsync(
            hub,
            new LiveUiEventDto(
                "PortScanRequested",
                m.TargetId,
                m.AssetId,
                "workers",
                $"Port scan requested: {m.HostOrIp}",
                m.OccurredAtUtc),
            context.CancellationToken);
    }
}

public sealed class SubdomainEnumerationRequestedUiEventConsumer(IHubContext<DiscoveryHub> hub) : IConsumer<SubdomainEnumerationRequested>
{
    public Task Consume(ConsumeContext<SubdomainEnumerationRequested> context)
    {
        var m = context.Message;
        return CommandCenterUiEventPublisher.PublishAsync(
            hub,
            new LiveUiEventDto(
                "SubdomainEnumerationRequested",
                m.TargetId,
                m.TargetId,
                "workers",
                $"Enumeration requested via {m.Provider}: {m.RootDomain}",
                m.OccurredAtUtc),
            context.CancellationToken);
    }
}
