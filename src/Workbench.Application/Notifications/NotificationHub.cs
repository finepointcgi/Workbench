using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;

namespace Workbench.Application.Notifications;

/// <summary>
/// In-process fan-out of new notifications, keyed by recipient email. Blazor circuits subscribe through
/// <see cref="InProcessNotificationFeed"/>; the web host's SignalR relay uses <see cref="SubscribeAll"/>.
/// </summary>
public sealed class NotificationHub(ILogger<NotificationHub>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<NotificationHub>.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Func<NotificationDto, Task>>> _byUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<string, NotificationDto, Task>> _all = [];

    public IAsyncDisposable Subscribe(string email, Func<NotificationDto, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Keyed by tenant as well: the same email in two tenants is two different people.
        var key = Tenancy.TenantScope.Qualify(email);
        lock (_gate)
        {
            if (!_byUser.TryGetValue(key, out var handlers))
            {
                _byUser[key] = handlers = [];
            }

            handlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_byUser.TryGetValue(key, out var handlers) && handlers.Remove(handler) && handlers.Count == 0)
                {
                    _byUser.Remove(key);
                }
            }
        });
    }

    /// <summary>Receives every user's notifications (with the recipient's email) until disposed.</summary>
    public IAsyncDisposable SubscribeAll(Func<string, NotificationDto, Task> handler)
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

    public async Task PublishAsync(string recipientEmail, NotificationDto notification)
    {
        Func<NotificationDto, Task>[] users;
        Func<string, NotificationDto, Task>[] all;
        lock (_gate)
        {
            users = _byUser.TryGetValue(Tenancy.TenantScope.Qualify(recipientEmail), out var handlers) ? [.. handlers] : [];
            all = [.. _all];
        }

        foreach (var handler in users)
        {
            await SafeAsync(() => handler(notification));
        }

        foreach (var handler in all)
        {
            await SafeAsync(() => handler(recipientEmail, notification));
        }
    }

    private async Task SafeAsync(Func<Task> call)
    {
        // Handlers run as the subscriber, not as the rule that created the notification; and a disconnected
        // circuit must not fail the change that created it.
        using var _ = Automation.AutomationContext.Suspend();
        try
        {
            await call();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A notification handler failed.");
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

/// <summary><see cref="INotificationFeed"/> for the web host: the current user's notifications from <see cref="NotificationHub"/>.</summary>
public sealed class InProcessNotificationFeed(NotificationHub hub, ICurrentUser currentUser) : INotificationFeed
{
    public Task<IAsyncDisposable> SubscribeAsync(Func<NotificationDto, Task> handler, CancellationToken cancellationToken = default) =>
        Task.FromResult(hub.Subscribe(currentUser.Email, handler));
}
