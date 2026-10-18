using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>
/// DCAA daily time recording: reminds employees who entered no hours for the previous business day. Idempotent: the
/// reminder for a given day goes out once per employee (an existing <see cref="NotificationKinds.TimesheetMissing"/>
/// notification with the same link counts as sent), so it can run as often as the host likes.
/// </summary>
public sealed class MissingTimesheetCheck(IWorkbenchDbContextFactory dbFactory, INotificationPublisher publisher, TimekeepingClock clock)
{
    /// <summary>The business day before <paramref name="today"/> (Friday for a Monday).</summary>
    public static DateOnly PreviousBusinessDay(DateOnly today)
    {
        var day = today.AddDays(-1);
        while (!TimekeepingClock.IsBusinessDay(day))
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    public static string LinkFor(DateOnly workDate) => $"time?date={workDate:yyyy-MM-dd}";

    /// <summary>
    /// Sends the reminders for the previous business day once the configured hour has passed. Returns how many went out.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (clock.LocalTime(clock.UtcNow).Hour < clock.Options.MissingTimeReminderHour)
        {
            return 0;
        }

        return await RunForAsync(PreviousBusinessDay(clock.Today), cancellationToken);
    }

    /// <summary>Reminds every employee employed on <paramref name="workDate"/> who has no hours on it and wasn't reminded yet.</summary>
    public async Task<int> RunForAsync(DateOnly workDate, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employees = (await db.Set<Employee>().AsNoTracking().Where(e => e.HireDate <= workDate).ToListAsync(cancellationToken))
            .Where(e => e.IsActiveOn(workDate))
            .ToList();
        if (employees.Count == 0)
        {
            return 0;
        }

        var entries = await db.Set<TimeEntry>().AsNoTracking()
            .Where(e => e.WorkDate == workDate)
            .Join(db.Set<Timesheet>(), e => e.TimesheetId, t => t.Id, (e, t) => new { t.EmployeeId, e.Hours })
            .ToListAsync(cancellationToken);
        var withHours = entries.GroupBy(e => e.EmployeeId).Where(g => g.Sum(e => e.Hours) > 0).Select(g => g.Key).ToHashSet();

        var link = LinkFor(workDate);
        var alreadyReminded = (await db.Set<Notification>().AsNoTracking()
                .Where(n => n.Kind == NotificationKinds.TimesheetMissing && n.Link == link)
                .Select(n => n.RecipientEmail)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sent = 0;
        foreach (var employee in employees.Where(e => !withHours.Contains(e.Id) && !alreadyReminded.Contains(e.Email)))
        {
            if (await publisher.PublishAsync(employee.Email, NotificationKinds.TimesheetMissing,
                    $"No hours entered for {workDate:dddd, MMM d}",
                    "Record your time every day, including indirect and leave hours.",
                    link))
            {
                sent++;
            }
        }

        return sent;
    }
}

/// <summary>Runs <see cref="MissingTimesheetCheck"/> hourly; the check itself decides whether anything is due.</summary>
public sealed class MissingTimesheetBackgroundService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MissingTimesheetBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<MissingTimesheetCheck>().RunAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Sent {Count} missing-timesheet reminders", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Missing-timesheet check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
