namespace Workbench.Application.Odcs;

public enum OdcNotificationKind { NeedsApproval, Approved, Rejected }

/// <summary>A notification about an ODC or expense report. <see cref="Link"/> is an app-relative URL.</summary>
public sealed record OdcNotification(OdcNotificationKind Kind, string RecipientEmail, string Subject, string Message, string Link);

/// <summary>
/// Sends ODC and expense notifications ("needs approval", "approved", "rejected"). The default does nothing;
/// the rules/notifications feature replaces it with an adapter over its INotificationPublisher.
/// </summary>
public interface IOdcNotifier
{
    Task NotifyAsync(OdcNotification notification, CancellationToken cancellationToken = default);
}

public sealed class NullOdcNotifier : IOdcNotifier
{
    public Task NotifyAsync(OdcNotification notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
