using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Reports;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Reports;

public sealed partial class StandardReportService
{
    // Labor distribution: hours by employee × project × period, limited to the employees whose time the user may see
    // (Timekeeper/Finance/Admin everyone, supervisors their direct reports, otherwise themselves). Labor cost (hours × the
    // cost rate on the work date) is a Finance/Admin column, as on the labor distribution export.

    private async Task LaborDistributionAsync(ReportBuilder b, ReportAccessContext context, ReportParameters p, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(p, ReportDateRange.ThisMonth, capAtToday: false, ct);
        b.Subtitle = $"{ReportDates.Describe(from, to)} · {(p.IncludeUnapproved ? "all entered hours" : "approved hours")}";
        b.Column("employee", "Employee").Column("employee_number", "Employee #").Column("project", "Project").Column("project_name", "Project name")
            .Column("period", "Period").Column("hours", "Hours", ReportColumnType.Hours);
        if (context.IsFinance)
        {
            b.Column("cost", "Labor cost", ReportColumnType.Money);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var entries = await LoadTimeAsync(db, context, from, to, p.EmployeeId, p.ProjectId, approvedOnly: !p.IncludeUnapproved, ct);
        var lookups = await TimeLookups.LoadAsync(db, entries.Select(e => e.EmployeeId), entries.Select(e => e.WbsElementId), context.IsFinance, ct);
        var rows = entries
            .Select(e =>
            {
                var wbs = lookups.Wbs[e.WbsElementId];
                var rate = context.IsFinance ? lookups.CostRates.Find(e.EmployeeId, e.WorkDate) : null;
                return new { e.EmployeeId, wbs.ProjectId, Period = PeriodLabel(e.WorkDate, p.Grouping, lookups, from, to), e.Hours, Cost = rate is { } r ? ReportValues.Cents(r * e.Hours) : 0m, Missing = context.IsFinance && rate is null };
            })
            .ToList();
        if (rows.Any(r => r.Missing))
        {
            b.Note("Some hours have no cost rate on their work date; their labor cost is counted as zero.");
        }

        foreach (var byEmployee in rows.GroupBy(r => r.EmployeeId).OrderBy(g => lookups.Employees[g.Key].DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var employee = lookups.Employees[byEmployee.Key];
            foreach (var cell in byEmployee.GroupBy(r => (r.ProjectId, r.Period.Sort, r.Period.Label))
                         .OrderBy(g => lookups.Projects[g.Key.ProjectId].Code, StringComparer.Ordinal).ThenBy(g => g.Key.Sort))
            {
                var project = lookups.Projects[cell.Key.ProjectId];
                var drill = DrillKey.Of(("employee", employee.Id), ("project", project.Id), ("from", cell.Min(c => c.Period.From)), ("to", cell.Max(c => c.Period.To)));
                Labor(b, context, ReportRowKind.Detail, 1, drill, employee.DisplayName, employee.EmployeeNumber, project.Code, project.Name, cell.Key.Label, cell.Sum(c => c.Hours), cell.Sum(c => c.Cost));
            }

            Labor(b, context, ReportRowKind.Subtotal, 0, null, employee.DisplayName, employee.EmployeeNumber, null, null, null, byEmployee.Sum(r => r.Hours), byEmployee.Sum(r => r.Cost));
        }

        Labor(b, context, ReportRowKind.Total, 0, null, "Total", null, null, null, null, rows.Sum(r => r.Hours), rows.Sum(r => r.Cost));
        if (!context.IsFinance)
        {
            b.Note(context.VisibleEmployees is { Count: <= 1 } ? "Shows your own hours. Labor cost is shown to Finance and administrators." : "Shows your hours and your direct reports'. Labor cost is shown to Finance and administrators.");
        }
    }

    private static void Labor(ReportBuilder b, ReportAccessContext context, ReportRowKind kind, int level, string? drill, params object?[] values) =>
        b.Add(kind, level, drill, null, context.IsFinance ? values : values[..^1]);

    private sealed record PeriodBucket(int Sort, string Label, DateOnly From, DateOnly To);

    private static PeriodBucket PeriodLabel(DateOnly date, ReportPeriodGrouping grouping, TimeLookups lookups, DateOnly from, DateOnly to)
    {
        switch (grouping)
        {
            case ReportPeriodGrouping.FiscalPeriod when lookups.Periods.FirstOrDefault(x => x.Start <= date && x.End >= date) is { } period:
                return new PeriodBucket(period.Start.DayNumber, period.Name, Max(period.Start, from), Min(period.End, to));
            case ReportPeriodGrouping.FiscalPeriod:
            case ReportPeriodGrouping.Month:
                var first = new DateOnly(date.Year, date.Month, 1);
                return new PeriodBucket(first.DayNumber, first.ToString("yyyy-MM", CultureInfo.InvariantCulture), Max(first, from), Min(first.AddMonths(1).AddDays(-1), to));
            case ReportPeriodGrouping.Week:
                var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
                return new PeriodBucket(monday.DayNumber, $"Week of {monday:yyyy-MM-dd}", Max(monday, from), Min(monday.AddDays(6), to));
            default:
                return new PeriodBucket(0, ReportDates.Describe(from, to), from, to);
        }
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private sealed record TimeRow(Guid EntryId, Guid TimesheetId, Guid EmployeeId, Guid WbsElementId, DateOnly WorkDate, decimal Hours, string? Comment, TimesheetStatus Status, Guid LaborCategoryId);

    /// <summary>Time entries in the range for the employees the user may see (optionally one employee and one project).</summary>
    private static async Task<List<TimeRow>> LoadTimeAsync(
        IWorkbenchDbContext db, ReportAccessContext context, DateOnly from, DateOnly to, Guid? employeeId, Guid? projectId, bool approvedOnly, CancellationToken ct)
    {
        if (employeeId is { } only && !context.CanSeeEmployeeTime(only))
        {
            throw new WorkbenchForbiddenException("You can see only your own hours and your direct reports'.");
        }

        var query = from e in db.Set<TimeEntry>().AsNoTracking()
                    join t in db.Set<Timesheet>() on e.TimesheetId equals t.Id
                    join w in db.Set<WbsElement>() on e.WbsElementId equals w.Id
                    where e.WorkDate >= @from && e.WorkDate <= to && (!approvedOnly || t.Status == TimesheetStatus.Approved)
                    select new { e, t, w };
        if (context.VisibleEmployees is { } visible)
        {
            var ids = visible.ToList();
            query = query.Where(x => ids.Contains(x.t.EmployeeId));
        }

        if (employeeId is { } emp)
        {
            query = query.Where(x => x.t.EmployeeId == emp);
        }

        if (projectId is { } proj)
        {
            query = query.Where(x => x.w.ProjectId == proj);
        }

        return await query.Select(x => new TimeRow(x.e.Id, x.t.Id, x.t.EmployeeId, x.e.WbsElementId, x.e.WorkDate, x.e.Hours, x.e.Comment, x.t.Status, x.e.LaborCategoryId)).ToListAsync(ct);
    }

    private async Task LaborDrillAsync(ReportBuilder b, ReportAccessContext context, ReportParameters p, DrillKey key, CancellationToken ct)
    {
        var (rangeFrom, rangeTo) = await RangeAsync(p, ReportDateRange.ThisMonth, capAtToday: false, ct);
        var from = key.Date("from") ?? rangeFrom;
        var to = key.Date("to") ?? rangeTo;
        await using var db = await dbFactory.CreateAsync(ct);
        var entries = await LoadTimeAsync(db, context, from, to, key.RequireId("employee"), key.Id("project"), approvedOnly: !p.IncludeUnapproved, ct);
        var lookups = await TimeLookups.LoadAsync(db, entries.Select(e => e.EmployeeId).Append(key.RequireId("employee")), entries.Select(e => e.WbsElementId), context.IsFinance, ct);
        b.Subtitle = $"{lookups.Employees.GetValueOrDefault(key.RequireId("employee"))?.DisplayName} · {ReportDates.Describe(from, to)}";
        b.Column("date", "Work date", ReportColumnType.Date).Column("wbs", "WBS").Column("wbs_name", "WBS name").Column("labor_category", "Labor category")
            .Column("status", "Timesheet status").Column("comment", "Comment").Column("hours", "Hours", ReportColumnType.Hours);
        if (context.IsFinance)
        {
            b.Column("rate", "Cost rate", ReportColumnType.Money).Column("cost", "Labor cost", ReportColumnType.Money);
        }

        foreach (var e in entries.OrderBy(e => e.WorkDate).ThenBy(e => lookups.Wbs[e.WbsElementId].Code, StringComparer.Ordinal))
        {
            var w = lookups.Wbs[e.WbsElementId];
            object?[] values = [e.WorkDate, w.Code, w.Name, lookups.Categories.GetValueOrDefault(e.LaborCategoryId), e.Status, e.Comment, e.Hours];
            if (context.IsFinance)
            {
                var rate = lookups.CostRates.Find(e.EmployeeId, e.WorkDate);
                values = [.. values, rate, rate is { } r ? ReportValues.Cents(r * e.Hours) : null];
            }

            b.Add(ReportRowKind.Detail, 0, null, $"time/history/{e.TimesheetId}", values);
        }
    }

    // Timesheet compliance (DCAA): for each employee the user may see and each timesheet period in the range, whether the
    // timesheet is missing (the period has ended and there's no timesheet, or it's in draft or rejected), was first submitted after the period ended, how
    // many days were entered later than the business-day limit, and how many entry changes were made after submission.

    private async Task TimesheetComplianceAsync(ReportBuilder b, ReportAccessContext context, ReportParameters p, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(p, ReportDateRange.ThisMonth, capAtToday: true, ct);
        var today = clock.Today;
        b.Subtitle = $"Timesheet periods overlapping {ReportDates.Describe(from, to)}";
        b.Column("employee", "Employee").Column("period_start", "Period start", ReportColumnType.Date).Column("period_end", "Period end", ReportColumnType.Date)
            .Column("status", "Status").Column("hours", "Hours", ReportColumnType.Hours).Column("submitted", "First submitted", ReportColumnType.Date)
            .Column("missing", "Missing", ReportColumnType.Boolean).Column("late_submission", "Submitted late", ReportColumnType.Boolean)
            .Column("late_days", "Days entered late", ReportColumnType.Number).Column("revisions", "Changes after submission", ReportColumnType.Number)
            .Column("reopened", "Reopened after approval", ReportColumnType.Number).Column("issues", "Issues");

        if (p.EmployeeId is { } onlyEmployee && !context.CanSeeEmployeeTime(onlyEmployee))
        {
            throw new WorkbenchForbiddenException("You can see only your own timesheets and your direct reports'.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var periods = new List<(DateOnly Start, DateOnly End)>();
        for (var d = from; d <= to;)
        {
            var period = await clock.PeriodForAsync(db, d, ct);
            periods.Add(period);
            d = period.End.AddDays(1);
        }

        var first = periods[0].Start;
        var last = periods[^1].End;
        var employees = (await db.Set<Employee>().AsNoTracking().OrderBy(e => e.DisplayName).ToListAsync(ct))
            .Where(e => context.CanSeeEmployeeTime(e.Id) && (p.EmployeeId is null || e.Id == p.EmployeeId))
            .Where(e => e.HireDate <= last && (e.TerminationDate is null || e.TerminationDate >= first))
            .ToList();
        var employeeIds = employees.Select(e => e.Id).ToList();
        var sheets = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => employeeIds.Contains(t.EmployeeId) && t.PeriodEnd >= first && t.PeriodStart <= last)
            .ToListAsync(ct);
        var sheetIds = sheets.Select(t => t.Id).ToList();
        var hours = (await db.Set<TimeEntry>().AsNoTracking().Where(e => sheetIds.Contains(e.TimesheetId)).Select(e => new { e.TimesheetId, e.Hours }).ToListAsync(ct))
            .GroupBy(e => e.TimesheetId).ToDictionary(g => g.Key, g => g.Sum(e => e.Hours));
        var revisions = (await db.Set<TimeEntryRevision>().AsNoTracking().Where(r => sheetIds.Contains(r.TimesheetId)).Select(r => r.TimesheetId).ToListAsync(ct))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var lateDays = (await timeReports.GetDailyEntryComplianceAsync(first, last, ct)).Where(r => r.IsLate).ToList();

        int missing = 0, late = 0, revised = 0, lateEntries = 0;
        foreach (var employee in employees)
        {
            foreach (var (start, end) in periods.Where(x => employee.HireDate <= x.End && (employee.TerminationDate is null || employee.TerminationDate >= x.Start)))
            {
                var sheet = sheets.FirstOrDefault(t => t.EmployeeId == employee.Id && t.PeriodStart <= start && t.PeriodEnd >= start);
                var ended = end < today;
                var isMissing = ended && (sheet is null || sheet.Status is TimesheetStatus.Draft or TimesheetStatus.Rejected);
                var submittedOn = sheet?.FirstSubmittedAtUtc is { } s ? clock.LocalDate(s) : (DateOnly?)null;
                var isLate = submittedOn is { } on && on > end;
                var days = lateDays.Count(r => r.EmployeeId == employee.Id && r.WorkDate >= start && r.WorkDate <= end);
                var changes = sheet is null ? 0 : revisions.GetValueOrDefault(sheet.Id);
                var reopened = sheet?.Revision ?? 0;
                var issues = new List<string>();
                if (isMissing) { issues.Add("missing"); missing++; }
                if (isLate) { issues.Add("submitted late"); late++; }
                if (days > 0) { issues.Add($"{days} day{(days == 1 ? "" : "s")} entered late"); lateEntries++; }
                if (changes > 0 || reopened > 0) { issues.Add("revised"); revised++; }
                b.Add(ReportRowKind.Detail, 0, sheet is null ? null : DrillKey.Of(("timesheet", sheet.Id)), sheet is null ? null : $"time/history/{sheet.Id}",
                    employee.DisplayName, start, end, sheet is null ? (ended ? "Not started" : "Open") : sheet.Status, sheet is null ? 0m : hours.GetValueOrDefault(sheet.Id),
                    submittedOn, isMissing, isLate, days, changes, reopened, issues.Count == 0 ? "OK" : string.Join(", ", issues));
            }
        }

        b.Note($"{missing} missing, {late} submitted late, {lateEntries} with days entered late (more than {clock.Options.LateEntryBusinessDays} business day{(clock.Options.LateEntryBusinessDays == 1 ? "" : "s")} after the work day), {revised} revised.");
        if (context.VisibleEmployees is { } visible)
        {
            b.Note(visible.Count <= 1 ? "Shows your own timesheets." : "Shows your own timesheets and your direct reports'.");
        }
    }

    private async Task ComplianceDrillAsync(ReportBuilder b, ReportAccessContext context, DrillKey key, CancellationToken ct)
    {
        var id = key.RequireId("timesheet");
        await using var db = await dbFactory.CreateAsync(ct);
        var sheet = await db.Set<Timesheet>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Timesheet", id);
        if (!context.CanSeeEmployeeTime(sheet.EmployeeId))
        {
            throw WorkbenchNotFoundException.For("Timesheet", id);
        }

        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, ct);
        b.Subtitle = $"{employee.DisplayName} · {sheet.PeriodStart:yyyy-MM-dd} to {sheet.PeriodEnd:yyyy-MM-dd}";
        var revisions = await db.Set<TimeEntryRevision>().AsNoTracking().Where(r => r.TimesheetId == id).OrderBy(r => r.ChangedAtUtc).ToListAsync(ct);
        var wbsIds = revisions.SelectMany(r => new[] { r.OldWbsElementId, r.NewWbsElementId }).OfType<Guid>().Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var days = await db.Set<TimeDayRecord>().AsNoTracking().Where(d => d.TimesheetId == id).OrderBy(d => d.WorkDate).ToListAsync(ct);

        b.Column("kind", "Kind").Column("date", "Work date", ReportColumnType.Date).Column("changed", "When", ReportColumnType.Date).Column("by", "By")
            .Column("old_hours", "Old hours", ReportColumnType.Hours).Column("new_hours", "New hours", ReportColumnType.Hours)
            .Column("old_wbs", "Old WBS").Column("new_wbs", "New WBS").Column("detail", "Reason / detail");
        foreach (var r in revisions)
        {
            b.Add(ReportRowKind.Detail, 0, null, null, "Revision", r.WorkDate, r.ChangedAtUtc, r.ChangedBy, r.OldHours, r.NewHours,
                r.OldWbsElementId is { } o ? wbs.GetValueOrDefault(o) : null, r.NewWbsElementId is { } n ? wbs.GetValueOrDefault(n) : null, r.Reason);
        }

        foreach (var d in days.Where(d => clock.IsLate(d.WorkDate, d.FirstEnteredAtUtc)))
        {
            var after = TimekeepingClock.BusinessDaysAfter(d.WorkDate, clock.LocalDate(d.FirstEnteredAtUtc));
            b.Add(ReportRowKind.Detail, 0, null, null, "Entered late", d.WorkDate, d.FirstEnteredAtUtc, employee.DisplayName, null, null, null, null,
                $"First entered {after} business day{(after == 1 ? "" : "s")} after the work day");
        }

        if (b.RowCount == 0)
        {
            b.Note("No revisions or late entries on this timesheet.");
        }
    }
}
