using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;

namespace Workbench.Application.Odcs;

public enum OdcNotificationKind { NeedsApproval, Approved, Rejected }

/// <summary>A notification about an ODC or expense report. <see cref="Link"/> is an app-relative URL.</summary>
public sealed record OdcNotification(OdcNotificationKind Kind, string RecipientEmail, string Subject, string Message, string Link);

/// <summary>
/// Sends ODC and expense notifications ("needs approval" to the approver, "approved" / "rejected" to the submitter).
/// The registered implementation is <see cref="PublishingOdcNotifier"/>.
/// </summary>
public interface IOdcNotifier
{
    Task NotifyAsync(OdcNotification notification, CancellationToken cancellationToken = default);
}

public sealed class PublishingOdcNotifier(INotificationPublisher publisher) : IOdcNotifier
{
    public Task NotifyAsync(OdcNotification notification, CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(notification.RecipientEmail, KindFor(notification.Kind), notification.Subject, notification.Message, notification.Link);

    public static string KindFor(OdcNotificationKind kind) => kind switch
    {
        OdcNotificationKind.Approved => NotificationKinds.ExpenseApproved,
        OdcNotificationKind.Rejected => NotificationKinds.ExpenseRejected,
        _ => NotificationKinds.ExpenseApproval,
    };
}
