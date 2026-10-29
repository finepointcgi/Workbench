using Microsoft.EntityFrameworkCore;
using Workbench.Application.MyTodo;
using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>Which of the three nudges a timesheet period is due for today.</summary>
public enum TimesheetReminderStage
{
    None,

    /// <summary>The business day before the due date.</summary>
    DayBefore,

    /// <summary>The due date.</summary>
    Due,

    /// <summary>After the period ended and the timesheet still isn't in (sent once, then it stops).</summary>
    Overdue,
}

/// <summary>
/// "Your timesheet isn't in": for each employee whose timesheet for the current period is missing or still a draft, one nudge the business
/// day before the period's due date, one on it, and one when it is overdue, then nothing more. Never on weekends, holidays or approved
/// leave, never once submitted, never in the person's quiet hours, at most one a day, and only once per occasion (<see cref="ReminderLog"/>).
/// Periods, "due" and "not in" are the Home to-do's own rules (<see cref="TimesheetTodoProvider"/>), so the two never disagree.
/// </summary>
public sealed class TimesheetReminderCheck(IWorkbenchDbContextFactory dbFactory, TimekeepingClock clock, ReminderGate gate) : IReminderCheck
{
    public string Name => "timesheet";

    /// <summary>The due date is the period's last day, or the last business day before it when that falls on a weekend.</summary>
    public static DateOnly DueDate(DateOnly periodEnd)
    {
        var due = periodEnd;
        while (!TimekeepingClock.IsBusinessDay(due))
        {
            due = due.AddDays(-1);
        }

        return due;
    }

    public static DateOnly PreviousBusinessDay(DateOnly date)
    {
        var day = date.AddDays(-1);
        while (!TimekeepingClock.IsBusinessDay(day))
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    public static DateOnly NextBusinessDay(DateOnly date)
    {
        var day = date.AddDays(1);
        while (!TimekeepingClock.IsBusinessDay(day))
        {
            day = day.AddDays(1);
        }

        return day;
    }

    /// <summary>Which nudge a period ending on <paramref name="periodEnd"/> is due today. <paramref name="isPrevious"/>: the period before the current one.</summary>
    public static TimesheetReminderStage StageFor(DateOnly today, DateOnly periodEnd, bool isPrevious)
    {
        if (isPrevious)
        {
            // Overdue is one nudge, on the first business day after the due date; after that the Home to-do keeps it in front of them.
            return today > periodEnd && today == NextBusinessDay(DueDate(periodEnd)) ? TimesheetReminderStage.Overdue : TimesheetReminderStage.None;
        }

        var due = DueDate(periodEnd);
        return today == due ? TimesheetReminderStage.Due
            : today == PreviousBusinessDay(due) ? TimesheetReminderStage.DayBefore
            : TimesheetReminderStage.None;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!gate.IsReminderTime)
        {
            return 0;
        }

        var today = gate.Today;
        List<(Employee Employee, DateOnly Start, DateOnly End, TimesheetReminderStage Stage, decimal Hours, decimal Standard, bool Started)> due = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var (start, end) = await clock.PeriodForAsync(db, today, cancellationToken);
            var (previousStart, previousEnd) = await clock.PeriodForAsync(db, start.AddDays(-1), cancellationToken);
            var periods = new[]
            {
                (Start: start, End: end, Stage: StageFor(today, end, isPrevious: false)),
                (Start: previousStart, End: previousEnd, Stage: StageFor(today, previousEnd, isPrevious: true)),
            }.Where(p => p.Stage != TimesheetReminderStage.None).ToList();
            if (periods.Count == 0)
            {
                return 0;
            }

            var employees = (await db.Set<Employee>().AsNoTracking().Where(e => e.HireDate <= today).ToListAsync(cancellationToken))
                .Where(e => e.IsActiveOn(today) && e.Email.Length > 0)
                .ToList();
            var from = periods.Min(p => p.Start);
            var to = periods.Max(p => p.End);
            var sheets = await db.Set<Timesheet>().AsNoTracking().Where(t => t.PeriodEnd >= from && t.PeriodStart <= to).ToListAsync(cancellationToken);
            var hours = (await db.Set<TimeEntry>().AsNoTracking()
                    .Where(e => e.WorkDate >= from && e.WorkDate <= to)
                    .Join(db.Set<Timesheet>(), e => e.TimesheetId, t => t.Id, (e, t) => new { t.EmployeeId, e.WorkDate, e.Hours })
                    .ToListAsync(cancellationToken));

            foreach (var period in periods)
            {
                foreach (var employee in employees.Where(e => e.HireDate <= period.End))
                {
                    var mine = sheets.Where(t => t.EmployeeId == employee.Id && t.PeriodStart <= period.End && t.PeriodEnd >= period.Start).ToList();
                    // Same rule as the Home to-do: no timesheet or a draft needs a nudge; submitted, approved or returned for fixing do not.
                    if (mine.Count > 0 && mine.Any(t => t.Status != TimesheetStatus.Draft))
                    {
                        continue;
                    }

                    var entered = hours.Where(h => h.EmployeeId == employee.Id && h.WorkDate >= period.Start && h.WorkDate <= period.End).Sum(h => h.Hours);
                    due.Add((employee, period.Start, period.End, period.Stage, entered, TimekeepingClock.StandardHours(employee, period.Start, period.End), mine.Count > 0));
                }
            }
        }

        if (due.Count == 0)
        {
            return 0;
        }

        var emails = due.Select(d => ReminderGate.Normalize(d.Employee.Email)).Distinct().ToList();
        var preferences = await gate.PreferencesAsync(emails, cancellationToken);
        var away = await gate.UnavailableTodayAsync(emails, cancellationToken);
        var hour = gate.LocalHour;
        var sent = 0;
        // Most urgent first, so a person with two periods behind gets the one that matters and the day's single reminder is spent on it.
        foreach (var item in due.OrderByDescending(d => d.Stage))
        {
            var email = ReminderGate.Normalize(item.Employee.Email);
            var preference = preferences[email];
            if (!preference.TimesheetEnabled || away.Contains(email) || ReminderGate.InQuietHours(preference, hour))
            {
                continue;
            }

            if (!await gate.ClaimAsync(email, "timesheet", $"{item.Start:yyyy-MM-dd}:{item.Stage}", cancellationToken))
            {
                continue;
            }

            var (title, body) = Message(item.Stage, today, item.Start, item.End, item.Hours, item.Standard, item.Started);
            await gate.SendAsync(email, preference, ReminderKinds.Timesheet, title, body, $"time?date={item.Start:yyyy-MM-dd}");
            sent++;
        }

        return sent;
    }

    private static (string Title, string Body) Message(TimesheetReminderStage stage, DateOnly today, DateOnly start, DateOnly end, decimal hours, decimal standard, bool started)
    {
        var range = MyTodoRules.Range(start, end);
        var title = stage switch
        {
            TimesheetReminderStage.DayBefore => today.AddDays(1) == DueDate(end) ? "Your timesheet is due tomorrow" : $"Your timesheet is due {DueDate(end):dddd}",
            TimesheetReminderStage.Due => "Your timesheet is due today",
            _ => "Your timesheet is overdue",
        };
        var progress = started && hours > 0
            ? $"{MyTodoRules.Hours(hours)} of {MyTodoRules.Hours(standard)} hours entered for {range}, and it isn't submitted yet."
            : $"You haven't entered any time for {range} yet.";
        return (title, stage == TimesheetReminderStage.Overdue ? $"{progress} It was due {end:ddd MMM d}." : $"{progress} It's due {end:ddd MMM d}.");
    }
}
