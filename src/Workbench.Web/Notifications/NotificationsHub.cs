using Microsoft.AspNetCore.SignalR;
using Workbench.Application.Automation;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Application.Time;
using Workbench.Contracts.Collaboration;

namespace Workbench.Web.Notifications;

/// <summary>
/// Pushes the connected user's new notifications to desktop clients as <see cref="NotificationMethod"/> messages
/// carrying a <c>NotificationDto</c>. Each connection joins its user's group on connect. The browser UI subscribes
/// in-process instead.
/// </summary>
[Microsoft.AspNetCore.Authorization.Authorize]
public sealed class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string NotificationMethod = "Notification";

    /// <summary>The SignalR group for a user, qualified by the current tenant: the same email in two tenants is two people.</summary>
    public static string GroupFor(string email) => $"{TenantScope.Key}:user:{email.Trim().ToLowerInvariant()}";

    public override async Task OnConnectedAsync()
    {
        // The connection's own user: hub calls run outside the HTTP pipeline, where ICurrentUser has no principal.
        if (Workbench.Web.Identity.ClaimsUser.EmailOf(Context.User) is { } email)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(email));
        }

        await base.OnConnectedAsync();
    }
}

/// <summary>Forwards every notification from the in-process <see cref="NotificationHub"/> to its recipient's SignalR group.</summary>
internal sealed class NotificationRelay(NotificationHub notifications, IHubContext<NotificationsHub> hub) : IHostedService
{
    private IAsyncDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = notifications.SubscribeAll((email, notification) =>
            hub.Clients.Group(NotificationsHub.GroupFor(email)).SendAsync(NotificationsHub.NotificationMethod, notification));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }
}

public static class NotificationsHosting
{
    /// <summary>
    /// Server-side automation: the notifications hub relay, rule actions attributed to "Workbench Automation",
    /// the hourly date-trigger and digest services, and the daily missing-timesheet reminder. Call after the infrastructure (which registers ICurrentUser).
    /// </summary>
    public static IServiceCollection AddWorkbenchNotificationsHub(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddHostedService<NotificationRelay>();
        services.AddWorkbenchAutomationHosting();
        services.AddWorkbenchTimekeepingHosting();
        Workbench.Application.ContractDetail.ContractDetailRegistration.AddWorkbenchContractDetailHosting(services);
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchNotificationsHub(this IEndpointRouteBuilder app)
    {
        app.MapHub<NotificationsHub>(NotificationsHub.Path);
        return app;
    }
}
