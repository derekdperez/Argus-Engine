using ArgusEngine.CommandCenter.Contracts;
using ArgusEngine.CommandCenter.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using DiscoveryHubEvents = ArgusEngine.CommandCenter.Hubs.DiscoveryHubEvents;

namespace ArgusEngine.CommandCenter.Realtime;

public sealed class DiscoveryRealtimeClient(NavigationManager navigation) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private HubConnection? _connection;

    public event EventHandler<LiveUiEventDto>? DomainEventReceived;
    public event EventHandler<CommandCenterStatusSnapshot>? StatusChangedReceived;
    public event EventHandler? ConnectionStateChanged;

    public string State => _connection?.State.ToString() ?? HubConnectionState.Disconnected.ToString();

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_connection?.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_connection?.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                return;
            }

            _connection ??= CreateConnection();
            await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
            RaiseConnectionStateChanged();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task SubscribeTargetAsync(Guid? targetId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        if (_connection is null || targetId is null)
        {
            return;
        }

        await _connection.InvokeAsync("SubscribeTarget", targetId.Value, cancellationToken).ConfigureAwait(false);
    }

    private HubConnection CreateConnection()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri("/hubs/discovery"))
            .WithAutomaticReconnect()
            .Build();

        connection.On<LiveUiEventDto>(DiscoveryHubEvents.DomainEvent, evt =>
        {
            DomainEventReceived?.Invoke(this, evt);
        });

        connection.On<CommandCenterStatusSnapshot>(DiscoveryHubEvents.StatusChanged, snapshot =>
        {
            StatusChangedReceived?.Invoke(this, snapshot);
        });

        connection.On<string>(DiscoveryHubEvents.WorkerChanged, workerKey =>
        {
            DomainEventReceived?.Invoke(
                this,
                new LiveUiEventDto(
                    DiscoveryHubEvents.WorkerChanged,
                    null,
                    null,
                    "workers",
                    workerKey,
                    DateTimeOffset.UtcNow));
        });

        connection.On<string>(DiscoveryHubEvents.QueueChanged, queueKey =>
        {
            DomainEventReceived?.Invoke(
                this,
                new LiveUiEventDto(
                    DiscoveryHubEvents.QueueChanged,
                    null,
                    null,
                    "http",
                    queueKey,
                    DateTimeOffset.UtcNow));
        });

        connection.On<Guid, string>(DiscoveryHubEvents.TargetQueued, (targetId, summary) =>
        {
            DomainEventReceived?.Invoke(
                this,
                new LiveUiEventDto(
                    DiscoveryHubEvents.TargetQueued,
                    targetId,
                    targetId,
                    "targets",
                    summary,
                    DateTimeOffset.UtcNow));
        });

        connection.Closed += _ =>
        {
            RaiseConnectionStateChanged();
            return Task.CompletedTask;
        };

        connection.Reconnecting += _ =>
        {
            RaiseConnectionStateChanged();
            return Task.CompletedTask;
        };

        connection.Reconnected += _ =>
        {
            RaiseConnectionStateChanged();
            return Task.CompletedTask;
        };

        return connection;
    }

    private void RaiseConnectionStateChanged()
    {
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectionGate.Dispose();
    }
}
