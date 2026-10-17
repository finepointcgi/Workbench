using Microsoft.AspNetCore.SignalR.Client;
using Workbench.Contracts.Collaboration;

namespace Workbench.ApiClient;

/// <summary>
/// Live updates for the desktop app over the server's <c>/hubs/sheets</c> SignalR hub. One connection is shared
/// by all subscriptions; it joins a sheet's group while anyone in the app is subscribed to that sheet and
/// rejoins after reconnecting.
/// </summary>
public sealed class SignalRSheetLiveUpdates : ISheetLiveUpdates, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, List<Func<SheetChange, Task>>> _handlers = [];

    public SignalRSheetLiveUpdates(Uri serverBaseAddress)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(serverBaseAddress, "hubs/sheets"))
            .WithAutomaticReconnect()
            .Build();
        _connection.On<SheetChange>("SheetChanged", DispatchAsync);
        _connection.Reconnected += async _ =>
        {
            foreach (var sheetId in Snapshot().Keys)
            {
                await _connection.InvokeAsync("JoinSheet", sheetId);
            }
        };
    }

    public async Task<IAsyncDisposable> SubscribeAsync(Guid sheetId, Func<SheetChange, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken);
            }

            bool join;
            lock (_handlers)
            {
                join = !_handlers.TryGetValue(sheetId, out var handlers);
                if (join)
                {
                    _handlers[sheetId] = handlers = [];
                }

                handlers!.Add(handler);
            }

            if (join)
            {
                await _connection.InvokeAsync("JoinSheet", sheetId, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        return new Subscription(this, sheetId, handler);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task UnsubscribeAsync(Guid sheetId, Func<SheetChange, Task> handler)
    {
        await _gate.WaitAsync();
        try
        {
            bool leave;
            lock (_handlers)
            {
                leave = _handlers.TryGetValue(sheetId, out var handlers) && handlers.Remove(handler) && handlers.Count == 0;
                if (leave)
                {
                    _handlers.Remove(sheetId);
                }
            }

            if (leave && _connection.State == HubConnectionState.Connected)
            {
                await _connection.InvokeAsync("LeaveSheet", sheetId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DispatchAsync(SheetChange change)
    {
        if (!Snapshot().TryGetValue(change.SheetId, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(change);
            }
            catch (Exception)
            {
                // A failing page handler must not break the connection for the others.
            }
        }
    }

    private Dictionary<Guid, Func<SheetChange, Task>[]> Snapshot()
    {
        lock (_handlers)
        {
            return _handlers.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }
    }

    private sealed class Subscription(SignalRSheetLiveUpdates owner, Guid sheetId, Func<SheetChange, Task> handler) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await owner.UnsubscribeAsync(sheetId, handler);
            }
        }
    }
}
