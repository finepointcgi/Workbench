using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;

namespace Workbench.Application.Time;

/// <summary>Turns timesheet events into notifications through <see cref="INotificationPublisher"/>.</summary>
public sealed class PublishingTimesheetNotifier(INotificationPublisher publisher) : ITimesheetNotifier
{
    public async Task NotifyAsync(TimesheetNotification n, CancellationToken cancellationToken = default)
    {
        var period = $"Week of {n.PeriodStart:MMM d}: {n.Hours:0.0} h";
        var employeeLink = $"time?date={n.PeriodStart:yyyy-MM-dd}";
        foreach (var recipient in n.Recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isEmployee = string.Equals(recipient, n.EmployeeEmail, StringComparison.OrdinalIgnoreCase);
            var whose = isEmployee ? "your" : $"{n.EmployeeName}'s";
            var (kind, title, body, link) = n.Event switch
            {
                TimesheetEvent.Submitted => (NotificationKinds.TimesheetSubmitted, $"{n.EmployeeName} submitted a timesheet", period, "time/approvals"),
                TimesheetEvent.Approved => (NotificationKinds.TimesheetApproved, $"{n.ActorName} approved your timesheet", period, employeeLink),
                TimesheetEvent.Rejected => (NotificationKinds.TimesheetRejected, $"{n.ActorName} rejected your timesheet", $"{period}. Reason: {n.Reason}", employeeLink),
                _ => (NotificationKinds.TimesheetReopened, $"{n.ActorName} reopened {whose} timesheet", $"{period}. Reason: {n.Reason}",
                    isEmployee ? employeeLink : $"time/history/{n.TimesheetId}"),
            };
            await publisher.PublishAsync(recipient, kind, title, body, link);
        }
    }
}
