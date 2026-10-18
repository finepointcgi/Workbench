using Microsoft.EntityFrameworkCore;
using Workbench.Application.Time;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.MyTodo;

/// <summary>
/// The user's own timesheets: this period's (not started, days without hours, due at period end), anything rejected or
/// returned for changes, and earlier periods still not submitted. Reads only the signed-in employee's rows.
/// </summary>
public sealed class TimesheetTodoProvider(TimekeepingClock clock) : IMyTodoProvider
{
    /// <summary>The prefix the approval inbox puts on a "return for changes" rejection.</summary>
    public const string ReturnedPrefix = "Returned for changes:";

    /// <summary>Earlier periods still open are listed this far back.</summary>
    public const int LookBackDays = 62;

    public string Name => "Timesheets";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.User.EmployeeId is not { } me)
        {
            return [];
        }

        var today = context.Today;
        await using var db = await context.DbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == me, cancellationToken);
        if (employee is null || !employee.IsActiveOn(today))
        {
            return [];
        }

        var (start, end) = await clock.PeriodForAsync(db, today, cancellationToken);
        var from = today.AddDays(-LookBackDays);
        var sheets = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => t.EmployeeId == me && t.PeriodEnd >= from && t.PeriodStart <= today)
            .ToListAsync(cancellationToken);
        var current = sheets.FirstOrDefault(t => t.PeriodStart <= today && t.PeriodEnd >= today);
        var items = new List<MyTodoItem>();

        foreach (var earlier in sheets.Where(t => t.PeriodEnd < (current?.PeriodStart ?? start)).OrderBy(t => t.PeriodStart))
        {
            if (earlier.Status is TimesheetStatus.Draft or TimesheetStatus.Rejected)
            {
                items.Add(Problem(earlier, context, isCurrent: false));
            }
        }

        if (current is null)
        {
            items.Add(new MyTodoItem(
                $"timesheet:start:{start:yyyy-MM-dd}", TodoCategories.Timesheet,
                "Start your timesheet",
                $"You haven't entered any time for {MyTodoRules.Range(start, end)} yet. It's due {end:ddd MMM d}.",
                context.UrgencyFor(end), end, $"time?date={today:yyyy-MM-dd}", "Enter time", Priority: 10));
            return items;
        }

        if (current.Status is TimesheetStatus.Rejected)
        {
            items.Add(Problem(current, context, isCurrent: true));
            return items;
        }

        if (current.Status is not TimesheetStatus.Draft)
        {
            return items;
        }

        var entered = await db.Set<TimeEntry>().AsNoTracking()
            .Where(e => e.TimesheetId == current.Id)
            .Select(e => new { e.WorkDate, e.Hours })
            .ToListAsync(cancellationToken);
        var holidays = await db.Set<Holiday>().AsNoTracking()
            .Where(h => h.Date >= current.PeriodStart && h.Date <= current.PeriodEnd)
            .Select(h => h.Date)
            .ToListAsync(cancellationToken);
        var missing = MissingDays(current.PeriodStart, today, employee, entered.Where(e => e.Hours > 0).Select(e => e.WorkDate), holidays);
        var total = entered.Sum(e => e.Hours);
        var standard = TimekeepingClock.StandardHours(employee, current.PeriodStart, current.PeriodEnd);

        if (missing.Count > 0)
        {
            var days = string.Join(", ", missing.Take(4).Select(d => d.ToString("ddd MMM d"))) + (missing.Count > 4 ? $" and {missing.Count - 4} more" : "");
            items.Add(new MyTodoItem(
                $"timesheet:missing:{current.Id}", TodoCategories.Timesheet,
                missing.Count == 1 ? "Enter your hours for " + missing[0].ToString("dddd") : $"Enter your hours for {missing.Count} days",
                $"No hours yet for {days}. Time should be entered every day.",
                TodoUrgency.Overdue, missing[0], $"time?date={missing[0]:yyyy-MM-dd}", "Enter time", Priority: 10));
        }

        var dueSoon = context.UrgencyFor(current.PeriodEnd) is not TodoUrgency.Later;
        if (dueSoon || missing.Count == 0)
        {
            items.Add(new MyTodoItem(
                $"timesheet:submit:{current.Id}", TodoCategories.Timesheet,
                "Submit your timesheet",
                $"{MyTodoRules.Hours(total)} of {MyTodoRules.Hours(standard)} hours entered for {MyTodoRules.Range(current.PeriodStart, current.PeriodEnd)}. Due {current.PeriodEnd:ddd MMM d}.",
                context.UrgencyFor(current.PeriodEnd), current.PeriodEnd, $"time?date={today:yyyy-MM-dd}", "Open timesheet", Priority: 5));
        }

        return items;
    }

    /// <summary>Business days from the period start up to yesterday (today is still being worked) with no hours, skipping holidays.</summary>
    public static IReadOnlyList<DateOnly> MissingDays(DateOnly periodStart, DateOnly today, Employee employee, IEnumerable<DateOnly> daysWithHours, IEnumerable<DateOnly> holidays)
    {
        var have = daysWithHours.Concat(holidays).ToHashSet();
        var missing = new List<DateOnly>();
        for (var d = periodStart; d < today; d = d.AddDays(1))
        {
            if (TimekeepingClock.IsBusinessDay(d) && employee.IsActiveOn(d) && !have.Contains(d))
            {
                missing.Add(d);
            }
        }

        return missing;
    }

    private static MyTodoItem Problem(Timesheet sheet, MyTodoContext context, bool isCurrent)
    {
        var range = MyTodoRules.Range(sheet.PeriodStart, sheet.PeriodEnd);
        var link = $"time?date={sheet.PeriodStart:yyyy-MM-dd}";
        if (sheet.Status == TimesheetStatus.Rejected)
        {
            var reason = sheet.RejectionReason?.Trim() ?? "";
            var returned = reason.StartsWith(ReturnedPrefix, StringComparison.OrdinalIgnoreCase);
            var why = returned ? reason[ReturnedPrefix.Length..].Trim() : reason;
            return new MyTodoItem(
                $"timesheet:rejected:{sheet.Id}", TodoCategories.Timesheet,
                returned ? $"Make the changes your approver asked for ({range})" : $"Fix and resubmit your timesheet ({range})",
                (returned ? "Revision requested" : "Rejected") + (why.Length > 0 ? $": \"{why}\"" : "."),
                isCurrent ? TodoUrgency.Today : TodoUrgency.Overdue, isCurrent ? context.Today : sheet.PeriodEnd, link, "Fix timesheet", Priority: 20);
        }

        return new MyTodoItem(
            $"timesheet:late:{sheet.Id}", TodoCategories.Timesheet,
            $"Submit your timesheet for {range}",
            $"It was due {sheet.PeriodEnd:ddd MMM d} and hasn't been submitted.",
            TodoUrgency.Overdue, sheet.PeriodEnd, link, "Open timesheet", Priority: 15);
    }
}
