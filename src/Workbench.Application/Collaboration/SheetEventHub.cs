using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Contracts.Collaboration;

namespace Workbench.Application.Collaboration;

/// <summary>Publishes sheet changes after they're saved. Implemented by <see cref="SheetEventHub"/>.</summary>
public interface ISheetChangePublisher
{
    Task PublishAsync(SheetChange change);
}

/// <summary>
/// In-process live updates for the web host: a singleton that every Blazor circuit subscribes to.
/// The SignalR relay in the web host uses <see cref="SubscribeAll"/> to forward changes to desktop clients.
/// </summary>
public sealed class SheetEventHub(ILogger<SheetEventHub>? logger = null) : ISheetLiveUpdates, ISheetChangePublisher
{
    private readonly ILogger _logger = logger ?? NullLogger<SheetEventHub>.Instance;
    private readonly Lock _gate = new();
    // Keyed by tenant as well: two tenants' sheets never share subscribers, even with the same id.
    private readonly Dictionary<(string Tenant, Guid SheetId), List<Func<SheetChange, Task>>> _bySheet = [];
    private readonly List<Func<SheetChange, Task>> _all = [];

    public Task<IAsyncDisposable> SubscribeAsync(Guid sheetId, Func<SheetChange, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var key = (Tenancy.TenantScope.Key, sheetId);
        lock (_gate)
        {
            if (!_bySheet.TryGetValue(key, out var handlers))
            {
                _bySheet[key] = handlers = [];
            }

            handlers.Add(handler);
        }

        return Task.FromResult<IAsyncDisposable>(new Subscription(() =>
        {
            lock (_gate)
            {
                if (_bySheet.TryGetValue(key, out var handlers) && handlers.Remove(handler) && handlers.Count == 0)
                {
                    _bySheet.Remove(key);
                }
            }
        }));
    }

    /// <summary>Receives changes for every sheet of every tenant until disposed (the publisher's tenant is in <c>TenantScope</c>).</summary>
    public IAsyncDisposable SubscribeAll(Func<SheetChange, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _all.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _all.Remove(handler);
            }
        });
    }

    /// <summary>Number of handlers subscribed to one sheet (for tests and diagnostics).</summary>
    public int SubscriberCount(Guid sheetId)
    {
        lock (_gate)
        {
            return _bySheet.TryGetValue((Tenancy.TenantScope.Key, sheetId), out var handlers) ? handlers.Count : 0;
        }
    }

    public async Task PublishAsync(SheetChange change)
    {
        Func<SheetChange, Task>[] targets;
        lock (_gate)
        {
            targets = [.. _bySheet.TryGetValue((Tenancy.TenantScope.Key, change.SheetId), out var handlers) ? handlers : [], .. _all];
        }

        // One failing subscriber (e.g. a disconnected circuit) must not fail the change that was already saved.
        foreach (var handler in targets)
        {
            try
            {
                await handler(change);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A live-update handler failed for {Kind} on sheet {SheetId}.", change.Kind, change.SheetId);
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
