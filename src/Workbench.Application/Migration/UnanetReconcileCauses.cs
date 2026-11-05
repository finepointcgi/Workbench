using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Migration;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Migration;

/// <summary>
/// Adds a likely cause, in plain words, to each row of a Unanet reconciliation that doesn't match. It looks at what Workbench
/// has for the person and project (are they imported, is there a timesheet, is it approved, does it cross month end, is
/// the cost rate different) and says what to check; it never changes anything. The wording is a lead, not a verdict.
/// </summary>
public sealed class UnanetReconcileCauses(IWorkbenchDbContextFactory dbFactory)
{
    private sealed record Sheet(Guid EmployeeId, DateOnly Start, DateOnly End, TimesheetStatus Status);

    private sealed record Hours(Guid EmployeeId, string Project, TimesheetStatus Status, decimal Total);

    /// <summary>Hours on a project from timesheets that run across a month boundary: inside the month and outside it.</summary>
    private sealed record Straddle(Guid EmployeeId, string Project, decimal Inside, decimal Outside);

    public async Task<IReadOnlyList<ReconRowDto>> ExplainAsync(ReconReportKind kind, ReconScope scope, IReadOnlyList<ReconRowDto> rows, CancellationToken ct)
    {
        if (rows.All(r => r.Status == ReconRowStatus.Matched))
        {
            return rows;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var employees = (await db.Set<Employee>().AsNoTracking().ToListAsync(ct)).ToDictionary(e => e.EmployeeNumber.ToUpperInvariant());
        var projects = (await db.Set<Project>().AsNoTracking().Select(p => p.Code).ToListAsync(ct)).Select(c => c.ToUpperInvariant()).ToHashSet();
        var sheets = (await db.Set<Timesheet>().AsNoTracking().Where(t => t.PeriodEnd >= scope.From && t.PeriodStart <= scope.To)
            .Select(t => new Sheet(t.EmployeeId, t.PeriodStart, t.PeriodEnd, t.Status)).ToListAsync(ct));
        var hours = await (from e in db.Set<TimeEntry>().AsNoTracking()
                           join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                           join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                           join p in db.Set<Project>().AsNoTracking() on w.ProjectId equals p.Id
                           where e.WorkDate >= scope.From && e.WorkDate <= scope.To
                           group e by new { t.EmployeeId, p.Code, t.Status } into g
                           select new { g.Key.EmployeeId, g.Key.Code, g.Key.Status, Total = g.Sum(x => x.Hours) }).ToListAsync(ct);
        var entries = hours.Select(h => new Hours(h.EmployeeId, h.Code.ToUpperInvariant(), h.Status, h.Total)).ToList();
        var crossing = await (from e in db.Set<TimeEntry>().AsNoTracking()
                              join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                              join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                              join p in db.Set<Project>().AsNoTracking() on w.ProjectId equals p.Id
                              where t.Status == TimesheetStatus.Approved && t.PeriodEnd >= scope.From && t.PeriodStart <= scope.To && (t.PeriodStart < scope.From || t.PeriodEnd > scope.To)
                              select new { t.EmployeeId, p.Code, e.WorkDate, e.Hours }).ToListAsync(ct);
        var straddles = crossing.GroupBy(c => (c.EmployeeId, Code: c.Code.ToUpperInvariant()))
            .Select(g => new Straddle(g.Key.EmployeeId, g.Key.Code,
                g.Where(c => c.WorkDate >= scope.From && c.WorkDate <= scope.To).Sum(c => c.Hours),
                g.Where(c => c.WorkDate < scope.From || c.WorkDate > scope.To).Sum(c => c.Hours))).ToList();
        var month = scope.From.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        return rows.Select(row =>
        {
            if (row.Status == ReconRowStatus.Matched)
            {
                return row;
            }

            var byPerson = kind == ReconReportKind.UnanetTimesheets;
            var person = byPerson ? employees.GetValueOrDefault(Normal(row.KeyParts[0])) : null;
            var project = Normal(row.KeyParts[^1]).Split('.')[0];
            var who = person?.DisplayName ?? row.Label ?? (byPerson ? row.KeyParts[0] : "the team");
            var mine = entries.Where(h => h.Project == project && (person is null || h.EmployeeId == person.Id)).ToList();
            var cause = row.Status switch
            {
                ReconRowStatus.OnlyInCostpoint => MissingInWorkbench(byPerson, person, row, project, projects, sheets, entries, who, month),
                ReconRowStatus.OnlyInWorkbench => $"Workbench has {Fmt(Value(row, "Hours", w: true))} hours for {who} on {row.KeyParts[^1]} in {month} that Unanet's report doesn't list. "
                    + "It may be on another project in Unanet, or the Unanet report is filtered (for example billable time only, or different dates).",
                _ => Different(row, who, month, mine, straddles.Where(x => x.Project == project && (person is null || x.EmployeeId == person.Id)).ToList()),
            };
            return row with { Cause = cause };
        }).ToList();
    }

    private static string MissingInWorkbench(bool byPerson, Employee? person, ReconRowDto row, string project, HashSet<string> projects, List<Sheet> sheets, List<Hours> entries, string who, string month)
    {
        var unanetHours = Fmt(Value(row, "Hours"));
        if (!projects.Contains(project))
        {
            return $"Project {row.KeyParts[^1]} isn't in Workbench, so its {unanetHours} hours can't be there. Import projects and tasks, then the timesheets.";
        }

        if (byPerson && person is null)
        {
            return $"Person {row.KeyParts[0]} isn't in Workbench. Import people, then their timesheets.";
        }

        if (person is not null)
        {
            var theirs = sheets.Where(s => s.EmployeeId == person.Id).ToList();
            if (theirs.Count == 0)
            {
                return $"{who} has no timesheet in Workbench for {month}, but Unanet has {unanetHours} hours. Import the timesheets (Setup › Import › Unanet › Timesheets) or enter and approve them.";
            }

            if (theirs.All(s => s.Status != TimesheetStatus.Approved))
            {
                return $"{who}'s timesheet for {month} is {theirs[0].Status.ToString().ToLowerInvariant()} in Workbench (only approved time counts), but Unanet has it approved: {unanetHours} hours.";
            }

            if (entries.Any(h => h.EmployeeId == person.Id))
            {
                return $"{who} has time in Workbench for {month}, but none on {row.KeyParts[^1]}; Unanet has {unanetHours} hours there. The time may have been charged to a different project or task.";
            }
        }

        return $"Workbench has no approved time on {row.KeyParts[^1]} in {month}, but Unanet has {unanetHours} hours. Check the timesheets were imported and approved.";
    }

    private static string Different(ReconRowDto row, string who, string month, List<Hours> mine, List<Straddle> straddles)
    {
        var hours = row.Values.FirstOrDefault(v => v.Field == "Hours");
        if (hours is { WithinTolerance: false })
        {
            var diff = hours.Workbench - hours.Costpoint;
            var more = diff > 0 ? "Workbench has" : "Unanet has";
            var amount = Fmt(Math.Abs(diff));
            var unapproved = mine.Where(h => h.Status != TimesheetStatus.Approved).Sum(h => h.Total);
            if (diff < 0 && unapproved > 0)
            {
                return $"{more} {amount} more hours. Workbench counts only approved time and {Fmt(unapproved)} hours of {who}'s time on this project is not approved yet in Workbench.";
            }

            // Unanet may count a whole timesheet period in one month: Workbench's days outside the month (Unanet has more), or
            // its days inside the month that belong to a period Unanet counted in the other month (Workbench has more).
            var crossing = diff < 0 ? straddles.Sum(x => x.Outside) : straddles.Sum(x => x.Inside);
            if (crossing > 0 && Math.Abs(Math.Abs(diff) - crossing) < 0.01m)
            {
                return diff < 0
                    ? $"{more} {amount} more hours. That is the time {who} has just outside {month} on a timesheet period that crosses the month end: Unanet probably counts the whole period in this month."
                    : $"{more} {amount} more hours. That is {who}'s time in {month} on a timesheet period that starts or ends in another month: Unanet probably counted the whole period in the other month.";
            }

            if (Math.Abs(diff) % 8 == 0 || Math.Abs(diff) % 4 == 0)
            {
                var days = Math.Abs(diff) / 8m;
                return $"{more} {amount} more hours (about {Fmt(days)} day(s) of time). Open the row to compare day by day: a day may be missing, entered twice, or dated in the other month.";
            }

            return $"{more} {amount} more hours. Open the row to compare entry by entry.";
        }

        var cost = row.Values.FirstOrDefault(v => v.Field == "LaborCost");
        if (cost is { WithinTolerance: false })
        {
            var diff = cost.Workbench - cost.Costpoint;
            var h = hours?.Workbench ?? 0m;
            var rates = h > 0 && hours is { Costpoint: > 0 } ? $" Workbench costs this at about ${Fmt2(cost.Workbench / h)} an hour and Unanet at about ${Fmt2(cost.Costpoint / hours.Costpoint)}." : "";
            return $"Hours match but labor cost differs by ${Fmt2(Math.Abs(diff))} ({(diff > 0 ? "Workbench is higher" : "Unanet is higher")}). The cost rate is different.{rates} "
                + $"Check {who}'s cost rate history and effective dates (Setup › Import › Unanet › Cost rate history).";
        }

        return $"The figures differ by more than the tolerance. Open the row to compare it entry by entry.";
    }

    private static decimal Value(ReconRowDto row, string field, bool w = false) =>
        row.Values.FirstOrDefault(v => v.Field == field) is { } v ? (w ? v.Workbench : v.Costpoint) : 0m;

    private static string Normal(string key) => key.Trim().ToUpperInvariant();

    private static string Fmt(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Fmt2(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
