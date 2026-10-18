namespace Workbench.Application.Notifications;

/// <summary>
/// Sends a notification from any Application service (timesheets, expenses, rules, ...). It is saved for the
/// recipient, pushed live to their bell, and emailed according to their delivery preference (right away, in the
/// daily digest, or not at all). Inject it like any service; it's a singleton.
/// <para>
/// Call it after your change is saved: it never throws for delivery problems (email failures are logged), only
/// for invalid input. Invalid or blank emails are skipped. <paramref name="link"/> is app-relative, e.g.
/// <c>time/2026-10-12</c>; absolute URLs are refused. Use a <see cref="Contracts.Notifications.NotificationKinds"/>
/// constant (or your own short lower-case string) for <c>kind</c>.
/// </para>
/// </summary>
public interface INotificationPublisher
{
    /// <summary>Notifies one person. Returns false when the email isn't valid (nothing is sent).</summary>
    Task<bool> PublishAsync(
        string recipientEmail,
        string kind,
        string title,
        string? body = null,
        string? link = null,
        Guid? sheetId = null,
        Guid? itemId = null);

    /// <summary>Notifies each distinct valid email once. Returns the normalized emails notified.</summary>
    Task<IReadOnlyList<string>> PublishAsync(
        IEnumerable<string?> recipientEmails,
        string kind,
        string title,
        string? body = null,
        string? link = null,
        Guid? sheetId = null,
        Guid? itemId = null);
}
