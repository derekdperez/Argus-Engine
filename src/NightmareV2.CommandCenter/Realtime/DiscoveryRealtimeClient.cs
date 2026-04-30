using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using NightmareV2.CommandCenter.Hubs;
using NightmareV2.CommandCenter.Models;

namespace NightmareV2.CommandCenter.Realtime;

/// <summary>
/// Per-circuit SignalR client used by interactive Blazor pages to receive event-driven UI updates.
/// </summary>
public sealed class DiscoveryRealtimeClient(NavigationManager navigation) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;

    public event Action<object?, LiveUiEventDto>? DomainEventReceived;
    public event EventHandler? ConnectionStateChanged;

    public string State => _connection?.State.ToString() ?? "Disconnected";
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(navigation.ToAbsoluteUri("/hubs/discovery"))
                    .WithAutomaticReconnect()
                    .Build();

                _connection.On<LiveUiEventDto>(
                    DiscoveryHubEvents.DomainEvent,
                    evt => DomainEventReceived?.Invoke(this, evt));

                // Backward-compatible event used by the existing target create endpoint.
                _connection.On<Guid, string>(
                    DiscoveryHubEvents.TargetQueued,
                    (targetId, rootDomain) => DomainEventReceived?.Invoke(
                        this,
                        new LiveUiEventDto(
                            "TargetQueued",
                            targetId,
                            targetId,
                            "targets",
                            $"Target queued: {rootDomain}",
                            DateTimeOffset.UtcNow)));

                _connection.Reconnecting += _ =>
                {
                    ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                };
                _connection.Reconnected += _ =>
                {
                    ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                };
                _connection.Closed += _ =>
                {
                    ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                };
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubscribeTargetAsync(Guid? targetId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (targetId is { } id && _connection is not null)
            await _connection.InvokeAsync("SubscribeTarget", id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
