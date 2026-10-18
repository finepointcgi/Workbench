namespace Workbench.Contracts.Notifications;

public enum NotificationDelivery
{
    /// <summary>Email each notification as it happens.</summary>
    ImmediateEmail,

    /// <summary>One email a day with the unread ones (the default).</summary>
    DailyDigest,

    /// <summary>Only the bell in the app.</summary>
    InAppOnly,
}

/// <summary>Well-known notification kinds (the bell picks an icon by kind). Features may add their own.</summary>
public static class NotificationKinds
{
    public const string Rule = "rule";
    public const string Mention = "mention";
    public const string Assignment = "assignment";
    public const string TimesheetSubmitted = "timesheet.submitted";
    public const string TimesheetApproved = "timesheet.approved";
    public const string TimesheetRejected = "timesheet.rejected";
    public const string TimesheetMissing = "timesheet.missing";
    public const string ExpenseApproval = "expense.approval";
}

/// <summary><see cref="Link"/> is app-relative, e.g. <c>sheets/{sheetId}?item={itemId}</c>.</summary>
public sealed record NotificationDto(
    Guid Id,
    string Kind,
    string Title,
    string Body,
    string? Link,
    Guid? SheetId,
    Guid? ItemId,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc)
{
    public bool IsRead => ReadAtUtc is not null;
}

public sealed record NotificationPage(IReadOnlyList<NotificationDto> Items, int TotalCount, int UnreadCount);

public sealed record NotificationPreferenceDto(NotificationDelivery Delivery);

/// <summary>The current user's notifications.</summary>
public interface INotificationService
{
    /// <summary>Newest first.</summary>
    Task<NotificationPage> ListMineAsync(int skip = 0, int take = 20, bool unreadOnly = false, CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(CancellationToken cancellationToken = default);

    Task<NotificationPreferenceDto> GetPreferenceAsync(CancellationToken cancellationToken = default);

    Task<NotificationPreferenceDto> SetPreferenceAsync(NotificationPreferenceDto preference, CancellationToken cancellationToken = default);
}

/// <summary>
/// New notifications for the current user as they're created. The web host delivers them in-process;
/// the desktop app receives them over SignalR from <c>/hubs/notifications</c>. Handlers may run on any thread.
/// </summary>
public interface INotificationFeed
{
    Task<IAsyncDisposable> SubscribeAsync(Func<NotificationDto, Task> handler, CancellationToken cancellationToken = default);
}
