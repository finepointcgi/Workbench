using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>Capacity, the heat map, project staffing, utilization, open demand and "find available people".</summary>
public sealed class ResourcePlanningService(
    IWorkbenchDbContextFactory dbFactory,
    IResourceLeaveCalendar leaveCalendar,
    ResourcingAccess rules,
    IOptions<ResourcingOptions> options,
    TimeProvider clock) : IResourcePlanningService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private ResourcingOptions Options => options.Value;

    public async Task<IReadOnlyList<CapacityWeekDto>> GetCapacityAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds = null, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, from, to, cancellationToken);
        if (employeeIds is not null)
        {
            people = people.Where(p => employeeIds.Contains(p.Id)).ToList();
        }

        if (!await rules.SeesEveryoneAsync(cancellationToken))
        {
            var me = await rules.MeAsync(cancellationToken);
            people = people.Where(p => p.Id == me).ToList();
        }

        return (await CapacityAsync(people, CapacityCalculator.Weeks(from, to), cancellationToken)).SelectMany(c => c.Value).ToList();
    }

    public async Task<HeatMapDto> GetHeatMapAsync(HeatMapQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Weeks is < 1 or > 52)
        {
            throw new ArgumentException("Show between 1 and 52 weeks.");
        }

        var weeks = Enumerable.Range(0, query.Weeks).Select(i => CapacityCalculator.WeekStart(query.From).AddDays(7 * i)).ToList();
        var to = weeks[^1].AddDays(6);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, weeks[0], to, cancellationToken);
        if (query.OrgUnitId is { } org)
        {
            people = people.Where(p => p.OrgUnitId == org).ToList();
        }

        if (!await rules.SeesEveryoneAsync(cancellationToken))
        {
            var me = await rules.MeAsync(cancellationToken);
            people = people.Where(p => p.Id == me).ToList();
        }

        var (firstWeek, lastWeek) = (weeks[0], weeks[^1]);
        var allocations = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.WeekStart >= firstWeek && a.WeekStart <= lastWeek).ToListAsync(cancellationToken);
        if (query.ProjectId is { } projectId)
        {
            var onProject = allocations.Where(a => a.ProjectId == projectId).Select(a => a.EmployeeId).ToHashSet();
            people = people.Where(p => onProject.Contains(p.Id)).ToList();
        }

        var capacity = await CapacityAsync(people, weeks, cancellationToken);
        var allocated = allocations.GroupBy(a => (a.EmployeeId, a.WeekStart)).ToDictionary(g => g.Key, g => g.Sum(a => a.Hours));
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);
        var cats = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        var rows = people
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new HeatMapRowDto(
                p.Id, p.DisplayName,
                p.OrgUnitId is { } o ? orgs.GetValueOrDefault(o) : null,
                p.DefaultLaborCategoryId is { } c ? cats.GetValueOrDefault(c) : null,
                capacity[p.Id].Select(w =>
                {
                    var hours = allocated.GetValueOrDefault((p.Id, w.WeekStart));
                    return new HeatMapCellDto(w.WeekStart, w.Capacity, hours, CapacityCalculator.StatusOf(w.Capacity, hours, Options.FullThresholdPercent),
                        w.HolidayHours, w.LeaveHours);
                }).ToList()))
            .ToList();
        return new HeatMapDto(weeks, rows, Options.FullThresholdPercent);
    }

    public async Task<ProjectStaffingDto> GetProjectStaffingAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var notes = new List<string>();
        var version = await CurrentBudgetAsync(db, projectId, notes, cancellationToken);
        var budget = version is null
            ? []
            : await db.Set<BudgetLine>().AsNoTracking().Where(l => l.BudgetVersionId == version.Id && l.CostType == BudgetCostType.Labor).ToListAsync(cancellationToken);
        var wbsIds = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).Select(w => w.Id).ToListAsync(cancellationToken);
        var actuals = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId)
                select new { e.LaborCategoryId, e.Hours })
            .ToListAsync(cancellationToken);
        var thisWeek = CapacityCalculator.WeekStart(Today);
        var allocations = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.WeekStart >= thisWeek).ToListAsync(cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, cancellationToken);
        var cats = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
        Guid? CategoryOf(ResourceAllocation a) => a.LaborCategoryId ?? employees.GetValueOrDefault(a.EmployeeId)?.DefaultLaborCategoryId;

        var keys = budget.Select(l => l.LaborCategoryId).Concat(allocations.Select(CategoryOf)).Distinct().ToList();
        var rows = new List<StaffingRowDto>();
        foreach (var key in keys)
        {
            var budgetHours = budget.Where(l => l.LaborCategoryId == key).Sum(l => l.Hours);
            var actualHours = actuals.Where(a => a.LaborCategoryId == key).Sum(a => a.Hours);
            var remaining = Math.Max(0, budgetHours - actualHours);
            var mine = allocations.Where(a => CategoryOf(a) == key).ToList();
            var allocated = mine.Sum(a => a.Hours);
            var gap = remaining - allocated;
            var tolerance = Math.Max(1m, remaining * Options.StaffingTolerancePercent / 100m);
            var status = budgetHours == 0 && allocated > 0 ? StaffingGapStatus.NotBudgeted
                : gap > tolerance ? StaffingGapStatus.Understaffed
                : gap < -tolerance ? StaffingGapStatus.Overstaffed
                : StaffingGapStatus.Balanced;
            var people = mine.GroupBy(a => a.EmployeeId)
                .Select(g => $"{employees.GetValueOrDefault(g.Key)?.DisplayName ?? "?"} ({g.Sum(a => a.Hours):0.#} h)")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var label = key is { } k && cats.TryGetValue(k, out var cat) ? $"{cat.Code} · {cat.Name}" : "No labor category";
            rows.Add(new StaffingRowDto(key, label, budgetHours, actualHours, remaining, allocated, gap, status, people));
        }

        rows = rows.OrderByDescending(r => r.Gap).ThenBy(r => r.LaborCategory, StringComparer.OrdinalIgnoreCase).ToList();
        if (allocations.Count == 0)
        {
            notes.Add("Nobody is allocated to this project from this week on. Use \"Generate from plan\" or add allocations.");
        }

        return new ProjectStaffingDto(project.Id, project.Code, project.Name, version?.Name, Today, rows,
            rows.Sum(r => r.RemainingBudgetHours), rows.Sum(r => r.AllocatedHours), notes);
    }

    public async Task<UtilizationDto> GetUtilizationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, from, to, cancellationToken);
        var visible = await rules.UtilizationEmployeeIdsAsync(cancellationToken);
        if (visible is not null)
        {
            people = people.Where(p => visible.Contains(p.Id)).ToList();
        }

        var ids = people.Select(p => p.Id).ToList();
        var holidays = await leaveCalendar.GetHolidaysAsync(from, to, ids, cancellationToken);
        var leave = await leaveCalendar.GetApprovedLeaveAsync(from, to, ids, cancellationToken);
        var (first, last) = (from, to);
        var entries = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                where t.Status == TimesheetStatus.Approved && e.WorkDate >= first && e.WorkDate <= last && ids.Contains(t.EmployeeId)
                select new { t.EmployeeId, e.WorkDate, e.Hours, w.Kind })
            .ToListAsync(cancellationToken);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);

        // One fact per person-day: available hours, worked hours (direct + indirect, not leave) and direct hours.
        var facts = new List<(Employee Person, DateOnly Day, decimal Available, decimal Actual, decimal Direct)>();
        foreach (var person in people)
        {
            var available = CapacityCalculator.AvailableByDay(Capacity(person), from, to, holidays, leave);
            var worked = entries.Where(e => e.EmployeeId == person.Id && e.Kind != ChargeKind.Leave).GroupBy(e => e.WorkDate)
                .ToDictionary(g => g.Key, g => (Actual: g.Sum(e => e.Hours), Direct: g.Where(e => e.Kind == ChargeKind.Direct).Sum(e => e.Hours)));
            foreach (var day in available.Keys.Union(worked.Keys).OrderBy(d => d))
            {
                var w = worked.GetValueOrDefault(day);
                facts.Add((person, day, available.GetValueOrDefault(day), w.Actual, w.Direct));
            }
        }

        UtilizationRowDto Row(string key, string name, IEnumerable<(Employee Person, DateOnly Day, decimal Available, decimal Actual, decimal Direct)> group)
        {
            var list = group.ToList();
            var available = list.Sum(f => f.Available);
            var actual = list.Sum(f => f.Actual);
            var direct = list.Sum(f => f.Direct);
            return new UtilizationRowDto(key, name, available, actual, direct,
                available == 0 ? null : Math.Round(direct / available, 4), available == 0 ? null : Math.Round(actual / available, 4));
        }

        var byPerson = facts.GroupBy(f => f.Person.Id)
            .Select(g => Row(g.Key.ToString(), g.First().Person.DisplayName, g)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var byOrg = facts.GroupBy(f => f.Person.OrgUnitId)
            .Select(g => Row(g.Key?.ToString() ?? "", g.Key is { } o ? orgs.GetValueOrDefault(o) ?? "?" : "No org unit", g))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var byMonth = facts.GroupBy(f => new DateOnly(f.Day.Year, f.Day.Month, 1))
            .OrderBy(g => g.Key).Select(g => Row(g.Key.ToString("yyyy-MM"), g.Key.ToString("MMM yyyy"), g)).ToList();
        return new UtilizationDto(from, to, byPerson, byOrg, byMonth, Row("total", "Total", facts), 1m - Options.NonBillableAllowancePercent / 100m);
    }

    public async Task<IReadOnlyList<OpenDemandDto>> ListOpenDemandAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var projectQuery = db.Set<Project>().AsNoTracking().Where(p => p.Status != ProjectStatus.Closed);
        if (projectId is { } id)
        {
            projectQuery = projectQuery.Where(p => p.Id == id);
        }
        else if (!await rules.ReadsAllAllocationsAsync(cancellationToken))
        {
            var managed = (await rules.ManagedProjectIdsAsync(cancellationToken)).ToList();
            projectQuery = projectQuery.Where(p => managed.Contains(p.Id));
        }

        var projects = await projectQuery.ToListAsync(cancellationToken);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken);
        var currentStart = periods.FirstOrDefault(p => p.Start <= Today && Today <= p.End)?.Start ?? new DateOnly(Today.Year, Today.Month, 1);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, cancellationToken);
        var cats = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
        var result = new List<OpenDemandDto>();
        foreach (var project in projects)
        {
            var version = await CurrentBudgetAsync(db, project.Id, null, cancellationToken);
            if (version is null)
            {
                continue;
            }

            var lines = await db.Set<BudgetLine>().AsNoTracking()
                .Where(l => l.BudgetVersionId == version.Id && l.CostType == BudgetCostType.Labor && l.PeriodStart != null && l.PeriodStart >= currentStart)
                .ToListAsync(cancellationToken);
            if (lines.Count == 0)
            {
                continue;
            }

            var allocations = await db.Set<ResourceAllocation>().AsNoTracking()
                .Where(a => a.ProjectId == project.Id && a.WeekStart >= currentStart.AddDays(-6)).ToListAsync(cancellationToken);
            foreach (var group in lines.GroupBy(l => (l.LaborCategoryId, Start: l.PeriodStart!.Value)).OrderBy(g => g.Key.Start))
            {
                var period = periods.FirstOrDefault(p => p.Start == group.Key.Start);
                var end = period?.End ?? group.Key.Start.AddMonths(1).AddDays(-1);
                var budgetHours = group.Sum(l => l.Hours);
                var allocated = allocations
                    .Where(a => (a.LaborCategoryId ?? employees.GetValueOrDefault(a.EmployeeId)?.DefaultLaborCategoryId) == group.Key.LaborCategoryId)
                    .Sum(a => a.Hours * WorkdaysWithin(a.WeekStart, group.Key.Start, end) / 5m);
                var open = Math.Round(budgetHours - allocated, 2);
                if (open >= 0.5m)
                {
                    var label = group.Key.LaborCategoryId is { } c && cats.TryGetValue(c, out var cat) ? $"{cat.Code} · {cat.Name}" : "No labor category";
                    result.Add(new OpenDemandDto(project.Id, project.Code, group.Key.LaborCategoryId, label, group.Key.Start,
                        period?.Name ?? group.Key.Start.ToString("yyyy-MM"), budgetHours, Math.Round(allocated, 2), open));
                }
            }
        }

        return result.OrderBy(d => d.PeriodStart).ThenBy(d => d.ProjectCode, StringComparer.Ordinal).ThenByDescending(d => d.OpenHours).ToList();
    }

    public async Task<IReadOnlyList<AvailablePersonDto>> FindAvailablePeopleAsync(FindPeopleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRange(query.From, query.To);
        var weeks = CapacityCalculator.Weeks(query.From, query.To);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, weeks[0], query.To, cancellationToken);
        if (query.LaborCategoryId is { } cat)
        {
            var assigned = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => a.LaborCategoryId == cat).Select(a => a.EmployeeId).Distinct().ToListAsync(cancellationToken);
            people = people.Where(p => p.DefaultLaborCategoryId == cat || assigned.Contains(p.Id)).ToList();
        }

        if (!await rules.SeesEveryoneAsync(cancellationToken))
        {
            var me = await rules.MeAsync(cancellationToken);
            people = people.Where(p => p.Id == me).ToList();
        }

        var capacity = await CapacityAsync(people, weeks, cancellationToken);
        var (firstWeek, lastWeek) = (weeks[0], weeks[^1]);
        var allocated = (await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.WeekStart >= firstWeek && a.WeekStart <= lastWeek).ToListAsync(cancellationToken))
            .GroupBy(a => (a.EmployeeId, a.WeekStart)).ToDictionary(g => g.Key, g => g.Sum(a => a.Hours));
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);
        var cats = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, cancellationToken);
        return people
            .Select(p =>
            {
                var w = capacity[p.Id].Select(c =>
                {
                    var hours = allocated.GetValueOrDefault((p.Id, c.WeekStart));
                    return new AvailableWeekDto(c.WeekStart, c.Capacity, hours, Math.Max(0, c.Capacity - hours));
                }).ToList();
                return new AvailablePersonDto(p.Id, p.DisplayName, p.DefaultLaborCategoryId is { } c ? cats.GetValueOrDefault(c) : null,
                    p.OrgUnitId is { } o ? orgs.GetValueOrDefault(o) : null, w.Sum(x => x.Capacity), w.Sum(x => x.Allocated), w.Sum(x => x.Available), w);
            })
            .Where(p => p.AvailableHours >= query.MinAvailableHours)
            .OrderByDescending(p => p.AvailableHours).ThenBy(p => p.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int WorkdaysWithin(DateOnly weekStart, DateOnly from, DateOnly to) =>
        Enumerable.Range(0, 5).Count(i => weekStart.AddDays(i) >= from && weekStart.AddDays(i) <= to);

    private static async Task<List<Employee>> PeopleAsync(IWorkbenchDbContext db, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.Set<Employee>().AsNoTracking().Where(e => e.HireDate <= to && (e.TerminationDate == null || e.TerminationDate >= from)).ToListAsync(ct);

    private static CapacityPerson Capacity(Employee e) => new(e.Id, e.StandardHoursPerWeek, e.HireDate, e.TerminationDate);

    private async Task<Dictionary<Guid, IReadOnlyList<CapacityWeekDto>>> CapacityAsync(IReadOnlyList<Employee> people, IReadOnlyList<DateOnly> weeks, CancellationToken ct)
    {
        if (weeks.Count == 0 || people.Count == 0)
        {
            return people.ToDictionary(p => p.Id, _ => (IReadOnlyList<CapacityWeekDto>)[]);
        }

        var ids = people.Select(p => p.Id).ToList();
        var from = weeks[0];
        var to = weeks[^1].AddDays(6);
        var holidays = await leaveCalendar.GetHolidaysAsync(from, to, ids, ct);
        var leave = await leaveCalendar.GetApprovedLeaveAsync(from, to, ids, ct);
        return people.ToDictionary(p => p.Id, p => CapacityCalculator.Compute(Capacity(p), weeks, holidays, leave, Options.NonBillableAllowancePercent));
    }

    private static async Task<BudgetVersion?> CurrentBudgetAsync(IWorkbenchDbContext db, Guid projectId, List<string>? notes, CancellationToken ct)
    {
        var versions = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        var current = versions.Where(v => v.Kind == BudgetKind.Current).MaxBy(v => v.CreatedAtUtc);
        if (current is not null)
        {
            return current;
        }

        var baseline = versions.Where(v => v.Kind == BudgetKind.Baseline).MaxBy(v => v.CreatedAtUtc);
        notes?.Add(baseline is null ? "This project has no budget, so remaining hours are zero." : "There's no Current budget, so the Baseline is used.");
        return baseline;
    }

    private static void ValidateRange(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        if (to.DayNumber - from.DayNumber > 2 * 366)
        {
            throw new ArgumentException("That date range is too long (two years at most).");
        }
    }
}
