using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for timesheet flags: the analyst's timesheet that is waiting for pm@'s approval gets Saturday
/// hours, a late-entered day and a revision that moved hours between charge codes, so the approvals inbox and the
/// "Timesheet flags" report have something to show. The assistant itself stays off (an administrator turns it on).
/// </summary>
internal static class AssistantSeed
{
    private const string Reason = "Moved Monday's hours to the design task";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<TimeEntryRevision>().AnyAsync(r => r.Reason == Reason, cancellationToken)
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "analyst@example.com", cancellationToken) is not { } analyst)
        {
            return;
        }

        var sheet = await db.Set<Timesheet>().Include(t => t.Entries)
            .Where(t => t.EmployeeId == analyst.Id && t.Status == TimesheetStatus.Submitted)
            .OrderByDescending(t => t.PeriodStart)
            .FirstOrDefaultAsync(cancellationToken);
        var monday = sheet?.Entries.Where(e => e.WorkDate == sheet.PeriodStart).FirstOrDefault();
        var design = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Code == "1001.01.03", cancellationToken);
        if (sheet is null || monday is null || design is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Add(new TimeEntry
        {
            TimesheetId = sheet.Id, WbsElementId = monday.WbsElementId, LaborCategoryId = monday.LaborCategoryId, WorkDate = sheet.PeriodStart.AddDays(5),
            Hours = 5m, Comment = "Weekend cutover support", EnteredAtUtc = now, UpdatedAtUtc = now,
        });
        var from = monday.WbsElementId;
        monday.WbsElementId = design.Id;
        sheet.Revision = 1;
        sheet.FirstSubmittedAtUtc ??= now.AddDays(-1);
        db.Add(new TimeEntryRevision
        {
            TimeEntryId = monday.Id, TimesheetId = sheet.Id, WorkDate = monday.WorkDate, OldHours = monday.Hours, NewHours = monday.Hours,
            OldWbsElementId = from, NewWbsElementId = design.Id, TimesheetRevision = 1, ChangedBy = analyst.Email, ChangedAtUtc = now, Reason = Reason,
        });
        for (var d = 0; d < 5; d++)
        {
            var day = sheet.PeriodStart.AddDays(d);
            var entered = d == 1 ? sheet.PeriodStart.AddDays(6) : day;
            db.Add(new TimeDayRecord { EmployeeId = analyst.Id, TimesheetId = sheet.Id, WorkDate = day, FirstEnteredAtUtc = entered.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc) });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
