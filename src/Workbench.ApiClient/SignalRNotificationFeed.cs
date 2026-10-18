using Microsoft.AspNetCore.SignalR.Client;
using Workbench.Contracts.Notifications;

namespace Workbench.ApiClient;

/// <summary>
/// The current user's new notifications for the desktop app, over the server's <c>/hubs/notifications</c> hub.
/// The server puts each connection in its user's group, so one shared connection serves every subscriber.
/// </summary>
public sealed class SignalRNotificationFeed : INotificationFeed, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Func<NotificationDto, Task>> _handlers = [];

    public SignalRNotificationFeed(Uri serverBaseAddress)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(serverBaseAddress, "hubs/notifications"))
            .WithAutomaticReconnect()
            .Build();
        _connection.On<NotificationDto>("Notification", DispatchAsync);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(Func<NotificationDto, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken);
            }

            lock (_handlers)
            {
                _handlers.Add(handler);
            }
        }
        finally
        {
            _gate.Release();
        }

        return new Subscription(() =>
        {
            lock (_handlers)
            {
                _handlers.Remove(handler);
            }
        });
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task DispatchAsync(NotificationDto notification)
    {
        Func<NotificationDto, Task>[] handlers;
        lock (_handlers)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(notification);
            }
            catch (Exception)
            {
                // A failing component handler must not break the connection for the others.
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IAsyncDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
