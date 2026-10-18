using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using Workbench.Domain;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// Allocations: who is planned on which project, week by week. Permission checks (who may change which project) are in
/// the identity decorator; list scoping (what you may see) is here.
/// </summary>
public sealed class ResourceAllocationService(
    IWorkbenchDbContextFactory dbFactory,
    ResourcingAccess rules,
    AuditRecorder audit,
    TimeProvider clock) : IResourceAllocationService
{
    private const string EntityType = nameof(ResourceAllocation);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<IReadOnlyList<AllocationDto>> ListAsync(AllocationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRange(query.From, query.To);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var from = CapacityCalculator.WeekStart(query.From);
        var q = db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.WeekStart >= from && a.WeekStart <= query.To);
        if (query.ProjectId is { } projectId)
        {
            q = q.Where(a => a.ProjectId == projectId);
        }

        if (query.EmployeeId is { } employeeId)
        {
            q = q.Where(a => a.EmployeeId == employeeId);
        }

        if (!await rules.ReadsAllAllocationsAsync(cancellationToken))
        {
            var managed = (await rules.ManagedProjectIdsAsync(cancellationToken)).ToList();
            var me = await rules.MeAsync(cancellationToken);
            q = q.Where(a => managed.Contains(a.ProjectId) || a.EmployeeId == me);
        }

        return await ToDtosAsync(db, await q.ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<AllocationDto>> ListMineAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        var me = await rules.MeAsync(cancellationToken);
        if (me is null)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var start = CapacityCalculator.WeekStart(from);
        var rows = await db.Set<ResourceAllocation>().AsNoTracking()
            .Where(a => a.EmployeeId == me && a.WeekStart >= start && a.WeekStart <= to).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, rows, cancellationToken);
    }

    public async Task<IReadOnlyList<AllocationDto>> CreateAsync(SaveAllocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Weeks is < 1 or > 104)
        {
            throw new ArgumentException("Allocate between 1 and 104 weeks at a time.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (employee, category) = await ValidateAsync(db, request, cancellationToken);
        var hours = HoursFor(request, employee);
        var now = clock.GetUtcNow().UtcDateTime;
        var start = CapacityCalculator.WeekStart(request.WeekStart);
        var weeks = Enumerable.Range(0, request.Weeks).Select(i => start.AddDays(7 * i)).ToList();
        var existing = await db.Set<ResourceAllocation>()
            .Where(a => a.EmployeeId == employee.Id && a.ProjectId == request.ProjectId && a.WbsElementId == request.WbsElementId
                && a.LaborCategoryId == category && a.Source == AllocationSource.Manual && weeks.Contains(a.WeekStart))
            .ToListAsync(cancellationToken);
        var saved = new List<ResourceAllocation>();
        foreach (var week in weeks)
        {
            // Entering a person's hours for a week they already have on the same project and task replaces them.
            var allocation = existing.FirstOrDefault(a => a.WeekStart == week);
            if (allocation is null)
            {
                allocation = new ResourceAllocation
                {
                    EmployeeId = employee.Id, ProjectId = request.ProjectId, WbsElementId = request.WbsElementId, LaborCategoryId = category,
                    WeekStart = week, Source = AllocationSource.Manual, CreatedBy = audit.Actor, CreatedAtUtc = now,
                };
                db.Set<ResourceAllocation>().Add(allocation);
            }
            else
            {
                allocation.UpdatedBy = audit.Actor;
                allocation.UpdatedAtUtc = now;
            }

            allocation.Hours = hours;
            allocation.Percent = request.Hours is null ? request.Percent : null;
            allocation.Notes = Clean(request.Notes);
            saved.Add(allocation);
        }

        audit.Record(db, EntityType, saved[0].Id, "Created", after: new
        {
            employee.Email, request.ProjectId, request.WbsElementId, Weeks = request.Weeks, From = start, Hours = hours,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtosAsync(db, saved, cancellationToken);
    }

    public async Task<AllocationDto> UpdateAsync(Guid allocationId, SaveAllocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var allocation = await db.Set<ResourceAllocation>().FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Allocation", allocationId);
        var (employee, category) = await ValidateAsync(db, request, cancellationToken);
        var before = Snapshot(allocation);
        allocation.EmployeeId = employee.Id;
        allocation.ProjectId = request.ProjectId;
        allocation.WbsElementId = request.WbsElementId;
        allocation.LaborCategoryId = category;
        allocation.WeekStart = CapacityCalculator.WeekStart(request.WeekStart);
        allocation.Hours = HoursFor(request, employee);
        allocation.Percent = request.Hours is null ? request.Percent : null;
        allocation.Notes = Clean(request.Notes);

        // An edited plan allocation becomes manual, so regenerating from the plan doesn't undo the edit.
        allocation.Source = AllocationSource.Manual;
        allocation.SourceItemId = null;
        allocation.UpdatedBy = audit.Actor;
        allocation.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, EntityType, allocation.Id, "Updated", before, Snapshot(allocation));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [allocation], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid allocationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var allocation = await db.Set<ResourceAllocation>().FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Allocation", allocationId);
        audit.Record(db, EntityType, allocation.Id, "Deleted", before: Snapshot(allocation));
        db.Set<ResourceAllocation>().Remove(allocation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> CanManageAsync(Guid projectId, CancellationToken cancellationToken = default) => rules.CanManageProjectAsync(projectId, cancellationToken);

    public async Task<GenerateAllocationsResultDto> GenerateFromPlanAsync(Guid projectId, GenerateAllocationsRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new GenerateAllocationsRequest();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var sheetId = project.SheetId ?? throw new ArgumentException($"Project {project.Code} has no plan sheet.");
        var from = CapacityCalculator.WeekStart(request.From ?? Today);
        var to = request.To ?? project.Finish ?? from.AddYears(2);
        ValidateRange(from, to, maxDays: 3 * 366);

        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().ToListAsync(cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(cancellationToken);
        var plan = PlanAllocationGenerator.Generate(columns, items, employees, wbs, from, to);

        var replaced = await db.Set<ResourceAllocation>()
            .Where(a => a.ProjectId == projectId && a.Source == AllocationSource.Plan && a.WeekStart >= from && a.WeekStart <= to)
            .ToListAsync(cancellationToken);
        db.Set<ResourceAllocation>().RemoveRange(replaced);
        var now = clock.GetUtcNow().UtcDateTime;
        var categories = employees.ToDictionary(e => e.Id, e => e.DefaultLaborCategoryId);
        db.Set<ResourceAllocation>().AddRange(plan.Allocations.Select(a => new ResourceAllocation
        {
            EmployeeId = a.EmployeeId, ProjectId = projectId, WbsElementId = a.WbsElementId, LaborCategoryId = categories.GetValueOrDefault(a.EmployeeId),
            WeekStart = a.WeekStart, Hours = a.Hours, Source = AllocationSource.Plan, SourceItemId = a.ItemId, CreatedBy = audit.Actor, CreatedAtUtc = now,
            Notes = "Generated from the plan",
        }));
        var total = plan.Allocations.Sum(a => a.Hours);
        audit.Record(db, EntityType, projectId, "GeneratedFromPlan", after: new { project.Code, From = from, To = to, Created = plan.Allocations.Count, Replaced = replaced.Count, Hours = total });
        await db.SaveChangesAsync(cancellationToken);
        return new GenerateAllocationsResultDto(plan.Allocations.Count, replaced.Count, total, plan.Skipped);
    }

    private static async Task<(Employee Employee, Guid? Category)> ValidateAsync(IWorkbenchDbContext db, SaveAllocationRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, ct)
            ?? throw new ArgumentException("Pick a person.");
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct)
            ?? throw new ArgumentException("Pick a project.");
        if (project.Status == ProjectStatus.Closed)
        {
            throw new ArgumentException($"Project {project.Code} is closed.");
        }

        if (request.WbsElementId is { } wbsId && !await db.Set<WbsElement>().AnyAsync(w => w.Id == wbsId && w.ProjectId == project.Id, ct))
        {
            throw new ArgumentException($"That WBS element isn't on project {project.Code}.");
        }

        var category = request.LaborCategoryId ?? employee.DefaultLaborCategoryId;
        if (request.LaborCategoryId is { } catId && !await db.Set<LaborCategory>().AnyAsync(c => c.Id == catId, ct))
        {
            throw new ArgumentException("Unknown labor category.");
        }

        if (employee.TerminationDate is { } end && CapacityCalculator.WeekStart(request.WeekStart) > end)
        {
            throw new ArgumentException($"{employee.DisplayName} has left the company.");
        }

        return (employee, category);
    }

    private static decimal HoursFor(SaveAllocationRequest request, Employee employee)
    {
        if (request.Hours is { } hours)
        {
            return hours is < 0 or > 168 ? throw new ArgumentException("Hours per week must be between 0 and 168.") : Math.Round(hours, 2);
        }

        if (request.Percent is { } percent)
        {
            return percent is < 0 or > 200
                ? throw new ArgumentException("A percentage must be between 0 and 200.")
                : Math.Round(employee.StandardHoursPerWeek * percent / 100m, 2, MidpointRounding.AwayFromZero);
        }

        throw new ArgumentException("Enter hours per week or a percentage.");
    }

    private static void ValidateRange(DateOnly from, DateOnly to, int maxDays = 2 * 366)
    {
        if (to < from)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        if (to.DayNumber - from.DayNumber > maxDays)
        {
            throw new ArgumentException("That date range is too long.");
        }
    }

    private static string? Clean(string? notes) => string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 500)];

    private static object Snapshot(ResourceAllocation a) => new { a.EmployeeId, a.ProjectId, a.WbsElementId, a.LaborCategoryId, a.WeekStart, a.Hours, Source = a.Source.ToString() };

    internal static async Task<IReadOnlyList<AllocationDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyCollection<ResourceAllocation> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();
        var wbsIds = rows.Where(r => r.WbsElementId is not null).Select(r => r.WbsElementId!.Value).Distinct().ToList();
        var catIds = rows.Where(r => r.LaborCategoryId is not null).Select(r => r.LaborCategoryId!.Value).Distinct().ToList();
        var people = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var cats = await db.Set<LaborCategory>().AsNoTracking().Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        return rows
            .OrderBy(r => r.WeekStart).ThenBy(r => people.GetValueOrDefault(r.EmployeeId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => projects.GetValueOrDefault(r.ProjectId)?.Code, StringComparer.Ordinal)
            .Select(r => new AllocationDto(
                r.Id, r.EmployeeId, people.GetValueOrDefault(r.EmployeeId) ?? "?", r.ProjectId,
                projects.GetValueOrDefault(r.ProjectId)?.Code ?? "?", projects.GetValueOrDefault(r.ProjectId)?.Name ?? "",
                r.WbsElementId, r.WbsElementId is { } w ? wbs.GetValueOrDefault(w) : null,
                r.LaborCategoryId, r.LaborCategoryId is { } c ? cats.GetValueOrDefault(c) : null,
                r.WeekStart, r.Hours, r.Percent, (AllocationOrigin)(int)r.Source, r.Notes))
            .ToList();
    }
}

/// <summary>A week of plan-generated hours for one person on one WBS element.</summary>
public sealed record PlannedAllocation(Guid EmployeeId, Guid? WbsElementId, Guid ItemId, DateOnly WeekStart, decimal Hours);

public sealed record PlanAllocationResult(IReadOnlyList<PlannedAllocation> Allocations, IReadOnlyList<string> Skipped);

/// <summary>
/// Turns a plan sheet into weekly allocations: each leaf task with Start/Finish and a Person assignment spreads its
/// planned hours (the "Planned hours" Number column, split between the people assigned) evenly over its working days, or
/// books the person's standard day when there are no planned hours. Tasks map to WBS elements through
/// <see cref="WbsElement.ItemId"/> (the task's own, else the nearest ancestor's).
/// </summary>
public static class PlanAllocationGenerator
{
    private static readonly string[] PersonColumns = ["Owner", "Assigned to", "Assignee", "Person", "Resource"];
    private static readonly string[] HoursColumns = [BudgetService.PlannedHoursColumn, "Hours", "Work", "Effort", "Estimated hours"];

    public static PlanAllocationResult Generate(
        IReadOnlyList<Column> columns,
        IReadOnlyList<Item> items,
        IReadOnlyList<Employee> employees,
        IReadOnlyList<WbsElement> wbs,
        DateOnly from,
        DateOnly to)
    {
        var person = columns.Where(c => c.Type == ColumnType.Person)
            .OrderBy(c => Rank(PersonColumns, c.Name)).ThenBy(c => c.Order).FirstOrDefault();
        if (person is null)
        {
            throw new ArgumentException("The plan sheet has no Person column to take assignments from.");
        }

        var hoursColumn = columns.Where(c => c.Type == ColumnType.Number && Rank(HoursColumns, c.Name) < HoursColumns.Length)
            .OrderBy(c => Rank(HoursColumns, c.Name)).FirstOrDefault();
        var byEmail = employees.GroupBy(e => e.Email, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var wbsByItem = wbs.Where(w => w.ItemId is not null).GroupBy(w => w.ItemId!.Value).ToDictionary(g => g.Key, g => g.First().Id);
        var byId = items.ToDictionary(i => i.Id);
        var parents = items.Where(i => i.ParentId is not null).Select(i => i.ParentId!.Value).ToHashSet();

        var totals = new Dictionary<(Guid Employee, Guid? Wbs, DateOnly Week), (decimal Hours, Guid Item)>();
        var skipped = new List<string>();
        foreach (var item in items.Where(i => !parents.Contains(i.Id)).OrderBy(i => i.Order))
        {
            var emails = (item.Values.GetValueOrDefault(person.Id.ToString()) ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (emails.Length == 0)
            {
                continue;
            }

            if (item.Start is not { } start || item.Finish is not { } finish)
            {
                skipped.Add($"\"{item.Title}\" has no start or finish date.");
                continue;
            }

            var workDays = Enumerable.Range(0, finish.DayNumber - start.DayNumber + 1).Select(start.AddDays)
                .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
            if (workDays.Count == 0)
            {
                skipped.Add($"\"{item.Title}\" has no working days.");
                continue;
            }

            var planned = hoursColumn is not null && decimal.TryParse(item.Values.GetValueOrDefault(hoursColumn.Id.ToString()), NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h > 0
                ? h : (decimal?)null;
            var wbsId = WbsFor(item);
            foreach (var email in emails)
            {
                if (!byEmail.TryGetValue(email, out var employee))
                {
                    skipped.Add($"\"{item.Title}\": {email} isn't an employee.");
                    continue;
                }

                var perDay = planned is { } total ? total / emails.Length / workDays.Count : employee.StandardHoursPerWeek / 5m;
                foreach (var day in workDays.Where(d => d >= from && d <= to && employee.IsActiveOn(d)))
                {
                    var key = (employee.Id, wbsId, CapacityCalculator.WeekStart(day));
                    var current = totals.GetValueOrDefault(key, (0m, item.Id));
                    totals[key] = (current.Item1 + perDay, current.Item2);
                }
            }
        }

        var allocations = totals
            .Select(t => new PlannedAllocation(t.Key.Employee, t.Key.Wbs, t.Value.Item, t.Key.Week, Math.Round(t.Value.Hours, 2, MidpointRounding.AwayFromZero)))
            .Where(a => a.Hours > 0)
            .OrderBy(a => a.WeekStart).ThenBy(a => a.EmployeeId)
            .ToList();
        return new PlanAllocationResult(allocations, skipped);

        Guid? WbsFor(Item item)
        {
            for (Item? current = item; current is not null; current = current.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            {
                if (wbsByItem.TryGetValue(current.Id, out var id))
                {
                    return id;
                }
            }

            return null;
        }
    }

    private static int Rank(string[] names, string name)
    {
        var index = Array.FindIndex(names, n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? names.Length : index;
    }
}
