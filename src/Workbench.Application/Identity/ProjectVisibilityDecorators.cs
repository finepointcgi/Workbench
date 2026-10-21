using System.Text.RegularExpressions;
using Workbench.Contracts.Notifications;

namespace Workbench.Application.Identity;

/// <summary>
/// The bell and the notification list: a notification is sent to the people on a project when it happens, but someone
/// taken off the project later shouldn't keep reading about it. Notifications that link to a project the user can't see
/// now (<see cref="ProjectVisibility"/>) are left out of the list; marking read and preferences are unchanged.
/// </summary>
public sealed partial class ProjectScopedNotificationService(INotificationService inner, ProjectVisibility visibility) : INotificationService
{
    public async Task<NotificationPage> ListMineAsync(int skip = 0, int take = 20, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var page = await inner.ListMineAsync(skip, take, unreadOnly, cancellationToken);
        if (!page.Items.Any(n => ProjectOf(n.Link) is not null))
        {
            return page;
        }

        var kept = await visibility.FilterAsync(page.Items, n => ProjectOf(n.Link), cancellationToken);
        var dropped = page.Items.Count - kept.Count;
        return dropped == 0 ? page : page with
        {
            Items = kept,
            TotalCount = Math.Max(0, page.TotalCount - dropped),
            UnreadCount = Math.Max(0, page.UnreadCount - page.Items.Except(kept).Count(n => !n.IsRead)),
        };
    }

    public Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default) => inner.GetUnreadCountAsync(cancellationToken);

    public Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default) => inner.MarkReadAsync(notificationId, cancellationToken);

    public Task MarkAllReadAsync(CancellationToken cancellationToken = default) => inner.MarkAllReadAsync(cancellationToken);

    public Task<NotificationPreferenceDto> GetPreferenceAsync(CancellationToken cancellationToken = default) => inner.GetPreferenceAsync(cancellationToken);

    public Task<NotificationPreferenceDto> SetPreferenceAsync(NotificationPreferenceDto preference, CancellationToken cancellationToken = default) =>
        inner.SetPreferenceAsync(preference, cancellationToken);

    /// <summary>The project an app-relative link points at (<c>projects/{id}/...</c> or <c>setup/projects/{id}</c>), if any.</summary>
    public static Guid? ProjectOf(string? link) =>
        link is not null && ProjectLink().Match(link) is { Success: true } m && Guid.TryParse(m.Groups[1].Value, out var id) ? id : null;

    [GeneratedRegex(@"(?:^|/)projects/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex ProjectLink();
}
