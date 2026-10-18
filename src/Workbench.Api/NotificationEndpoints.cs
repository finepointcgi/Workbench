using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Notifications;

namespace Workbench.Api;

/// <summary>The current user's notifications (<c>/api/v1/me/notifications</c>).</summary>
internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this RouteGroupBuilder v1)
    {
        var mine = v1.MapGroup("/me/notifications").WithTags("Notifications");

        mine.MapGet("/", (int? skip, int? take, bool? unreadOnly, INotificationService service, CancellationToken ct) =>
            service.ListMineAsync(skip ?? 0, take ?? 20, unreadOnly ?? false, ct));

        mine.MapGet("/unread-count", (INotificationService service, CancellationToken ct) => service.GetUnreadCountAsync(ct));

        mine.MapPost("/{notificationId:guid}/read", async (Guid notificationId, INotificationService service, CancellationToken ct) =>
        {
            await service.MarkReadAsync(notificationId, ct);
            return Results.NoContent();
        });

        mine.MapPost("/read-all", async (INotificationService service, CancellationToken ct) =>
        {
            await service.MarkAllReadAsync(ct);
            return Results.NoContent();
        });

        mine.MapGet("/preference", (INotificationService service, CancellationToken ct) => service.GetPreferenceAsync(ct));

        mine.MapPut("/preference", (NotificationPreferenceDto preference, INotificationService service, CancellationToken ct) =>
            service.SetPreferenceAsync(preference, ct));
    }
}
