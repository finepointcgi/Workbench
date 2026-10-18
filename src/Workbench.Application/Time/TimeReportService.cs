using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>
/// Timekeeping reports. "Approved hours" are entries on timesheets whose status is Approved; a reopened timesheet drops
/// out until it is approved again.
/// TODO: restrict to the Finance role once authentication and roles land.
/// </summary>
public sealed class TimeReportService(IWorkbenchDbContextFactory dbFactory, TimekeepingClock clock) : ITimeReportService
{
    public async Task<IReadOnlyList<HoursByChargeCodeRow>> GetHoursByChargeCodeAsync(DateOnly from, DateOnly to, bool approvedOnly = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await LoadAsync(db, from, to, approvedOnly, cancellationToken);
        return rows.GroupBy(r => (r.Employee.Id, r.Wbs.Id))
            .Select(g => new HoursByChargeCodeRow(g.Key.Item1, g.First().Employee.DisplayName, g.Key.Item2, g.First().Wbs.Code, g.First().Project.Code, g.Sum(r => r.Entry.Hours)))
            .OrderBy(r => r.EmployeeName).ThenBy(r => r.WbsCode, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<DailyEntryComplianceRow>> GetDailyEntryComplianceAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var days = await db.Set<TimeDayRecord>().AsNoTracking().Where(d => d.WorkDate >= from && d.WorkDate <= to).ToListAsync(cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, cancellationToken);
        var hours = await db.Set<TimeEntry>().AsNoTracking()
            .Where(e => e.WorkDate >= from && e.WorkDate <= to)
            .Join(db.Set<Timesheet>(), e => e.TimesheetId, t => t.Id, (e, t) => new { t.EmployeeId, e.WorkDate, e.Hours })
            .ToListAsync(cancellationToken);
        var totals = hours.GroupBy(h => (h.EmployeeId, h.WorkDate)).ToDictionary(g => g.Key, g => g.Sum(h => h.Hours));
        return days
            .Select(d =>
            {
                var after = TimekeepingClock.BusinessDaysAfter(d.WorkDate, clock.LocalDate(d.FirstEnteredAtUtc));
                return new DailyEntryComplianceRow(d.EmployeeId, employees[d.EmployeeId].DisplayName, d.WorkDate, totals.GetValueOrDefault((d.EmployeeId, d.WorkDate)),
                    d.FirstEnteredAtUtc, after, after > clock.Options.LateEntryBusinessDays);
            })
            .OrderBy(r => r.EmployeeName).ThenBy(r => r.WorkDate)
            .ToList();
    }

    public async Task<string> ExportLaborDistributionCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await LoadAsync(db, from, to, approvedOnly: true, cancellationToken);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);
        var employeeIds = rows.Select(r => r.Employee.Id).Distinct().ToList();
        var rates = (await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(cancellationToken))
            .GroupBy(r => r.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).ToList());

        var csv = new StringBuilder("EmployeeNumber,Employee,WorkDate,Project,Wbs,LaborCategory,Hours,HourlyCost,Cost\r\n");
        foreach (var r in rows.OrderBy(r => r.Employee.EmployeeNumber).ThenBy(r => r.Entry.WorkDate).ThenBy(r => r.Wbs.Code, StringComparer.Ordinal))
        {
            // Labor cost = approved hours x the cost rate effective on the work date; never stored.
            var rate = rates.GetValueOrDefault(r.Employee.Id)?.FirstOrDefault(x => x.EffectiveFrom <= r.Entry.WorkDate)?.HourlyCost;
            var cost = rate is { } v ? decimal.Round(v * r.Entry.Hours, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
            csv.AppendJoin(',',
                Csv(r.Employee.EmployeeNumber), Csv(r.Employee.DisplayName), r.Entry.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Csv(r.Project.Code), Csv(r.Wbs.Code), Csv(categories.GetValueOrDefault(r.Entry.LaborCategoryId, "")),
                r.Entry.Hours.ToString("0.00", CultureInfo.InvariantCulture),
                rate?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "",
                cost?.ToString("0.00", CultureInfo.InvariantCulture) ?? "");
            csv.Append("\r\n");
        }

        return csv.ToString();
    }

    private static async Task<List<(TimeEntry Entry, Employee Employee, WbsElement Wbs, Project Project)>> LoadAsync(
        IWorkbenchDbContext db, DateOnly start, DateOnly end, bool approvedOnly, CancellationToken cancellationToken)
    {
        if (end < start)
        {
            throw new ArgumentException("The end date can't be before the start date.");
        }

        var query = from e in db.Set<TimeEntry>().AsNoTracking()
                    join t in db.Set<Timesheet>() on e.TimesheetId equals t.Id
                    where e.WorkDate >= start && e.WorkDate <= end && (!approvedOnly || t.Status == TimesheetStatus.Approved)
                    join emp in db.Set<Employee>() on t.EmployeeId equals emp.Id
                    join w in db.Set<WbsElement>() on e.WbsElementId equals w.Id
                    join p in db.Set<Project>() on w.ProjectId equals p.Id
                    select new { e, emp, w, p };
        return (await query.ToListAsync(cancellationToken)).Select(x => (x.e, x.emp, x.w, x.p)).ToList();
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
