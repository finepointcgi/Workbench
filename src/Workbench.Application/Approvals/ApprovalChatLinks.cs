using Workbench.Application.Notifications;
using Workbench.Domain.Notifications;

namespace Workbench.Application.Approvals;

/// <summary>
/// For a Teams message about something waiting on someone: link straight to the document (where the approve / return / reject bar is),
/// not just the inbox. Prefers the approval that became theirs when the notification was sent; otherwise the only one waiting.
/// </summary>
public sealed class ApprovalChatLinks(IWorkbenchDbContextFactory dbFactory, ApprovalEngine engine) : INotificationChatLinks
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    public async Task<string?> LinkAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var waiting = (await engine.ActionableAsync(db, null, ApprovalEngine.Normalize(notification.RecipientEmail), cancellationToken)).ToList();
        var fresh = waiting.Where(x => x.Tasks.Any(t => t.Task.ActivatedAtUtc is { } at && at >= notification.CreatedAtUtc - Window && at <= notification.CreatedAtUtc + TimeSpan.FromMinutes(1))).ToList();
        var pick = fresh.Count == 1 ? fresh[0] : waiting.Count == 1 ? waiting[0] : default;
        return pick.Instance?.Link is { Length: > 0 } link && !Uri.TryCreate(link, UriKind.Absolute, out _) ? link : null;
    }
}
