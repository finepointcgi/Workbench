using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// Budget against staffing: the labor budget (hours by labor category on each WBS element) compared with the people staffed
/// on it from this week on, the qualification and over-budget checks for staffing someone, and a project's setup progress.
/// Permission checks are in the identity decorator.
/// </summary>
public sealed class ProjectStaffingService(
    IWorkbenchDbContextFactory dbFactory,
    IResourceLeaveCalendar leaveCalendar,
    IOptions<ResourcingOptions> options,
    TimeProvider clock) : IProjectStaffingService
{
    /// <summary>How far ahead the overbooking check looks.</summary>
    public const int OverbookedWeeksAhead = 8;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ProjectStaffingPlanDto> GetPlanAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var facts = await LoadAsync(db, projectId, cancellationToken);
        var (categories, slots) = StaffingCalculator.Calculate(facts.Inputs, options.Value.StaffingTolerancePercent);
        var notes = new List<string>(facts.Notes);
        if (facts.Inputs.Allocations.Count == 0 && categories.Any(c => c.BudgetHours > 0))
        {
            notes.Add("Nobody is staffed on this project from this week on. Staff people into each labor category below.");
        }

        var overbooked = await OverbookedAsync(db, facts.Inputs.Allocations.Select(a => a.EmployeeId).Distinct().ToList(), cancellationToken);
        return new ProjectStaffingPlanDto(facts.Project.Id, facts.Project.Code, facts.Project.Name, facts.BudgetName, Today, categories, slots, overbooked, notes);
    }

    public async Task<ProjectSetupProgressDto> GetSetupProgressAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var facts = await LoadAsync(db, projectId, cancellationToken);
        var (categories, _) = StaffingCalculator.Calculate(facts.Inputs, options.Value.StaffingTolerancePercent);
        var chargeCodes = await db.Set<WbsElement>().AsNoTracking()
            .CountAsync(w => w.ProjectId == projectId && w.IsChargeable && w.IsOpen && w.Kind == ChargeKind.Direct, cancellationToken);
        var contract = facts.Project.ContractId is { } cid
            ? await db.Set<Contract>().AsNoTracking().Where(c => c.Id == cid).Select(c => c.ContractNumber).FirstOrDefaultAsync(cancellationToken)
            : null;
        return SetupSteps.Build(projectId, contract, chargeCodes, categories);
    }

    public async Task<StaffingCheckDto> CheckAsync(StaffingCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Weeks is < 1 or > 104)
        {
            throw new ArgumentException("Staff between 1 and 104 weeks at a time.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var facts = await LoadAsync(db, request.ProjectId, cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, cancellationToken)
            ?? throw new ArgumentException("Pick a person.");
        var category = request.LaborCategoryId ?? employee.DefaultLaborCategoryId;
        var categoryLabel = category is { } c && facts.Categories.TryGetValue(c, out var cat) ? Label(cat) : null;
        var qualified = await QualificationsAsync(db, [employee.Id], cancellationToken);
        var qualifies = StaffingCalculator.Qualifies(qualified, employee.Id, category);

        // Entering hours for weeks the person already has on the same WBS element and category replaces them.
        var start = CapacityCalculator.WeekStart(request.WeekStart);
        var weeks = Enumerable.Range(0, request.Weeks).Select(i => start.AddDays(7 * i)).ToHashSet();
        var thisWeek = CapacityCalculator.WeekStart(Today);
        var replaced = facts.Inputs.Allocations
            .Where(a => a.EmployeeId == employee.Id && a.WbsElementId == request.WbsElementId && a.LaborCategoryId == category && a.IsManual && weeks.Contains(a.WeekStart))
            .Sum(a => a.Hours);
        var added = Math.Max(0, request.HoursPerWeek) * weeks.Count(w => w >= thisWeek);
        var check = StaffingCalculator.Check(facts.Inputs, request.WbsElementId, category, added - replaced);

        var warnings = new List<StaffingWarningDto>();
        var where = request.WbsElementId is { } wbsId && facts.Wbs.TryGetValue(wbsId, out var wbs) ? $"on {wbs.Code}" : "on this project";
        if (category is null)
        {
            warnings.Add(new(StaffingWarningKind.NoLaborCategory,
                $"{employee.DisplayName} has no labor category, so these hours won't count against the budget. Pick the category they'll bill as."));
        }
        else if (!qualifies)
        {
            var own = employee.DefaultLaborCategoryId is { } o && facts.Categories.TryGetValue(o, out var ownCat) ? Label(ownCat) : "no labor category";
            warnings.Add(new(StaffingWarningKind.NotQualified,
                $"{employee.DisplayName} bills as {own}, not {categoryLabel}. Staff them in their own category, or check with Finance that they qualify for this one."));
        }

        if (category is not null && facts.ContractCategories is { } onContract && !onContract.Contains(category.Value))
        {
            warnings.Add(new(StaffingWarningKind.NotOnContract,
                $"{categoryLabel} has no bill rate on the contract, so these hours can't be billed at it."));
        }

        if (category is not null && check.BudgetHours == 0)
        {
            warnings.Add(new(StaffingWarningKind.NotBudgeted,
                $"There's no budget for {categoryLabel} {where}. Add it to the budget first, or pick a budgeted category."));
        }
        else if (category is not null && check.StaffedAfter > check.LeftHours + 0.005m)
        {
            warnings.Add(new(StaffingWarningKind.OverBudget,
                $"This staffs {Hours(check.StaffedAfter)} {categoryLabel} hours {where}, {Hours(check.StaffedAfter - check.LeftHours)} more than the {Hours(check.LeftHours)} left in the budget."));
        }

        return new StaffingCheckDto(category, categoryLabel, qualifies, check.LeftHours, check.StaffedAfter, warnings);
    }

    public async Task<IReadOnlyList<StaffingCandidateDto>> ListCandidatesAsync(Guid projectId, Guid? laborCategoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var today = Today;
        var people = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.TerminationDate == null || e.TerminationDate >= today).ToListAsync(cancellationToken);
        var qualified = await QualificationsAsync(db, people.Select(p => p.Id).ToList(), cancellationToken);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
        var thisWeek = CapacityCalculator.WeekStart(today);
        var last = thisWeek.AddDays(7 * 3);
        var booked = (await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.WeekStart >= thisWeek && a.WeekStart <= last).ToListAsync(cancellationToken))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => Math.Round(g.Sum(a => a.Hours) / 4m, 1));
        return people
            .Select(p => new StaffingCandidateDto(
                p.Id, p.DisplayName, p.DefaultLaborCategoryId,
                p.DefaultLaborCategoryId is { } c && categories.TryGetValue(c, out var cat) ? Label(cat) : null,
                laborCategoryId is not null && StaffingCalculator.Qualifies(qualified, p.Id, laborCategoryId),
                p.StandardHoursPerWeek, booked.GetValueOrDefault(p.Id)))
            .OrderByDescending(p => p.Qualifies)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string Label(LaborCategory category) => $"{category.Code} {category.Name}";

    internal static string Hours(decimal hours) => hours.ToString("#,##0.#", CultureInfo.InvariantCulture);

    /// <summary>The labor categories each person qualifies for: their own, and any they're assigned to charge in.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, HashSet<Guid>>> QualificationsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> employeeIds, CancellationToken ct)
    {
        var ids = employeeIds.ToList();
        var own = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id) && e.DefaultLaborCategoryId != null)
            .Select(e => new { e.Id, Category = e.DefaultLaborCategoryId!.Value }).ToListAsync(ct);
        var assigned = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => ids.Contains(a.EmployeeId) && a.LaborCategoryId != null)
            .Select(a => new { Id = a.EmployeeId, Category = a.LaborCategoryId!.Value }).ToListAsync(ct);
        return own.Concat(assigned).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Select(x => x.Category).ToHashSet());
    }

    private async Task<IReadOnlyList<OverbookedPersonDto>> OverbookedAsync(IWorkbenchDbContext db, IReadOnlyList<Guid> employeeIds, CancellationToken ct)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        var ids = employeeIds.ToList();
        var thisWeek = CapacityCalculator.WeekStart(Today);
        var weeks = Enumerable.Range(0, OverbookedWeeksAhead).Select(i => thisWeek.AddDays(7 * i)).ToList();
        var last = weeks[^1];
        var people = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        var allocated = (await db.Set<ResourceAllocation>().AsNoTracking()
                .Where(a => ids.Contains(a.EmployeeId) && a.WeekStart >= thisWeek && a.WeekStart <= last).ToListAsync(ct))
            .GroupBy(a => (a.EmployeeId, a.WeekStart)).ToDictionary(g => g.Key, g => g.Sum(a => a.Hours));
        var holidays = await leaveCalendar.GetHolidaysAsync(thisWeek, last.AddDays(6), ids, ct);
        var leave = await leaveCalendar.GetApprovedLeaveAsync(thisWeek, last.AddDays(6), ids, ct);
        var result = new List<OverbookedPersonDto>();
        foreach (var person in people)
        {
            // Booked past their working hours (standard less holidays and leave); the non-billable allowance isn't counted here.
            var capacity = CapacityCalculator.Compute(new CapacityPerson(person.Id, person.StandardHoursPerWeek, person.HireDate, person.TerminationDate), weeks, holidays, leave, 0m);
            var over = capacity
                .Select(c => (c.WeekStart, c.Capacity, Allocated: allocated.GetValueOrDefault((person.Id, c.WeekStart))))
                .Where(w => CapacityCalculator.IsOverbooked(w.Allocated, w.Capacity))
                .ToList();
            if (over.Count > 0)
            {
                var worst = over.MaxBy(w => w.Allocated - w.Capacity);
                result.Add(new OverbookedPersonDto(person.Id, person.DisplayName, over.Count, over[0].WeekStart, worst.Allocated, worst.Capacity));
            }
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record ProjectFacts(
        Project Project,
        string? BudgetName,
        StaffingInputs Inputs,
        IReadOnlyDictionary<Guid, WbsElement> Wbs,
        IReadOnlyDictionary<Guid, LaborCategory> Categories,
        IReadOnlySet<Guid>? ContractCategories,
        IReadOnlyList<string> Notes);

    private async Task<ProjectFacts> LoadAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var notes = new List<string>();
        var versions = await db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == projectId).ToListAsync(ct);
        var version = versions.Where(v => v.Kind == BudgetKind.Current).MaxBy(v => v.CreatedAtUtc)
            ?? versions.Where(v => v.Kind == BudgetKind.Baseline).MaxBy(v => v.CreatedAtUtc);
        if (version is null)
        {
            notes.Add(versions.Count == 0
                ? "This project has no budget yet. Budget it by labor category first, then staff people into those categories."
                : "Only forecast budget versions exist. Staffing is compared with the Current budget (or the Baseline).");
        }
        else if (version.Kind == BudgetKind.Baseline)
        {
            notes.Add($"There's no Current budget, so the Baseline \"{version.Name}\" is used.");
        }

        var versionId = version?.Id;
        var budget = versionId is null ? [] : await db.Set<BudgetLine>().AsNoTracking()
            .Where(l => l.BudgetVersionId == versionId && l.CostType == BudgetCostType.Labor)
            .Select(l => new StaffingBudget(l.WbsElementId, l.LaborCategoryId, l.Hours)).ToListAsync(ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, ct);
        var wbsIds = wbs.Keys.ToList();
        var charged = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId)
                select new StaffingCharge(e.WbsElementId, e.LaborCategoryId, e.Hours))
            .ToListAsync(ct);
        var thisWeek = CapacityCalculator.WeekStart(Today);
        var rows = await db.Set<ResourceAllocation>().AsNoTracking().Where(a => a.ProjectId == projectId && a.WeekStart >= thisWeek).ToListAsync(ct);
        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var allocations = rows.Select(a => new StaffingAllocation(
                a.EmployeeId, employees.GetValueOrDefault(a.EmployeeId)?.DisplayName ?? "?", a.WbsElementId,
                a.LaborCategoryId ?? employees.GetValueOrDefault(a.EmployeeId)?.DefaultLaborCategoryId, a.WeekStart, a.Hours, a.Source == AllocationSource.Manual))
            .ToList();
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var qualified = await QualificationsAsync(db, allocations.Select(a => a.EmployeeId).Distinct().ToList(), ct);
        IReadOnlySet<Guid>? onContract = null;
        if (project.ContractId is { } contractId)
        {
            var rates = await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contractId).Select(r => r.LaborCategoryId).ToListAsync(ct);
            onContract = rates.Count == 0 ? null : rates.ToHashSet(); // a contract without bill rates (e.g. fixed price) says nothing about categories
        }

        var inputs = new StaffingInputs(
            budget, charged, allocations, qualified,
            id => wbs.TryGetValue(id, out var w) ? (w.Code, w.Name) : ("?", ""),
            id => id is { } c && categories.TryGetValue(c, out var cat) ? Label(cat) : "No labor category");
        return new ProjectFacts(project, version?.Name, inputs, wbs, categories, onContract, notes);
    }
}

public sealed record StaffingBudget(Guid WbsElementId, Guid? LaborCategoryId, decimal Hours);

public sealed record StaffingCharge(Guid WbsElementId, Guid? LaborCategoryId, decimal Hours);

/// <summary>A week of someone's staffed hours on the project. <see cref="LaborCategoryId"/> is already resolved (the allocation's, else the person's).</summary>
public sealed record StaffingAllocation(Guid EmployeeId, string Name, Guid? WbsElementId, Guid? LaborCategoryId, DateOnly WeekStart, decimal Hours, bool IsManual = true);

public sealed record StaffingInputs(
    IReadOnlyList<StaffingBudget> Budget,
    IReadOnlyList<StaffingCharge> Charged,

    /// <summary>Allocations from this week on.</summary>
    IReadOnlyList<StaffingAllocation> Allocations,

    /// <summary>The labor categories each person qualifies for.</summary>
    IReadOnlyDictionary<Guid, HashSet<Guid>> Qualifications,
    Func<Guid, (string Code, string Name)> Wbs,
    Func<Guid?, string> Category);

/// <summary>Where a slot stands once a proposed number of hours is added.</summary>
public sealed record StaffingSlotCheck(decimal BudgetHours, decimal LeftHours, decimal StaffedAfter);

/// <summary>
/// The staffing arithmetic, kept free of the database so it can be tested directly. A slot is a WBS element × labor
/// category; left = max(0, budget − approved hours charged); gap = left − staffed. Within the tolerance (a share of what's
/// left, at least one hour) the slot is balanced.
/// </summary>
public static class StaffingCalculator
{
    public static (IReadOnlyList<StaffingCategoryDto> Categories, IReadOnlyList<StaffingSlotDto> Slots) Calculate(StaffingInputs inputs, decimal tolerancePercent)
    {
        // Hours charged where there's no budget show too, so nothing charged to the project is invisible here.
        var keys = inputs.Budget.Select(b => ((Guid?)b.WbsElementId, b.LaborCategoryId))
            .Concat(inputs.Allocations.Select(a => (a.WbsElementId, a.LaborCategoryId)))
            .Concat(inputs.Charged.Where(c => c.Hours != 0).Select(c => ((Guid?)c.WbsElementId, c.LaborCategoryId)))
            .Distinct()
            .ToList();
        var budgetedCategories = inputs.Budget.Where(b => b.Hours > 0).Select(b => b.LaborCategoryId).ToHashSet();
        var slots = new List<StaffingSlotDto>();
        foreach (var (wbsId, categoryId) in keys)
        {
            var budget = inputs.Budget.Where(b => b.WbsElementId == wbsId && b.LaborCategoryId == categoryId).Sum(b => b.Hours);
            var charged = wbsId is null ? 0 : inputs.Charged.Where(c => c.WbsElementId == wbsId && c.LaborCategoryId == categoryId).Sum(c => c.Hours);
            var left = Math.Max(0, budget - charged);
            var mine = inputs.Allocations.Where(a => a.WbsElementId == wbsId && a.LaborCategoryId == categoryId).ToList();
            var staffed = mine.Sum(a => a.Hours);
            var gap = left - staffed;

            // People staffed on the whole project count toward their category's total rather than a WBS element's budget.
            var status = wbsId is null && budgetedCategories.Contains(categoryId) && categoryId is not null
                ? StaffingGapStatus.Balanced
                : StatusOf(budget, left, staffed, tolerancePercent);
            var people = mine.GroupBy(a => a.EmployeeId)
                .Select(g => new StaffedPersonDto(g.Key, g.First().Name, g.Sum(a => a.Hours), Qualifies(inputs.Qualifications, g.Key, categoryId), g.Max(a => a.WeekStart).AddDays(6)))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var (code, name) = wbsId is { } w ? inputs.Wbs(w) : (null, null);
            slots.Add(new StaffingSlotDto(wbsId, code, name, categoryId, inputs.Category(categoryId), budget, charged, left, staffed, gap, status, people));
        }

        var categories = slots.GroupBy(s => s.LaborCategoryId)
            .Select(g =>
            {
                var budget = g.Sum(s => s.BudgetHours);
                var left = g.Sum(s => s.LeftHours);
                var staffed = g.Sum(s => s.StaffedHours);
                // Charged against the budget: hours on WBS elements without budget for the category show on their own slot.
                return new StaffingCategoryDto(g.Key, g.First().LaborCategory, budget, g.Where(s => s.BudgetHours > 0).Sum(s => s.ChargedHours), left, staffed, left - staffed,
                    StatusOf(budget, left, staffed, tolerancePercent));
            })
            .OrderByDescending(c => c.Gap).ThenBy(c => c.LaborCategory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ordered = slots
            .OrderBy(s => s.WbsCode is null ? 1 : 0).ThenBy(s => s.WbsCode, StringComparer.Ordinal)
            .ThenBy(s => s.LaborCategory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (categories, ordered);
    }

    /// <summary>Where the slot (WBS element × labor category) would stand with <paramref name="addedHours"/> more staffed hours.</summary>
    public static StaffingSlotCheck Check(StaffingInputs inputs, Guid? wbsElementId, Guid? laborCategoryId, decimal addedHours)
    {
        IEnumerable<StaffingBudget> budget = inputs.Budget.Where(b => b.LaborCategoryId == laborCategoryId);
        IEnumerable<StaffingCharge> charged = inputs.Charged.Where(c => c.LaborCategoryId == laborCategoryId);
        IEnumerable<StaffingAllocation> staffed = inputs.Allocations.Where(a => a.LaborCategoryId == laborCategoryId);
        if (wbsElementId is { } wbs)
        {
            budget = budget.Where(b => b.WbsElementId == wbs);
            charged = charged.Where(c => c.WbsElementId == wbs);
            staffed = staffed.Where(a => a.WbsElementId == wbs);
        }

        // Staffing on the whole project is checked against the category's total across the project.
        var budgetHours = budget.Sum(b => b.Hours);
        var left = Math.Max(0, budgetHours - charged.Sum(c => c.Hours));
        return new StaffingSlotCheck(budgetHours, left, Math.Max(0, staffed.Sum(a => a.Hours) + addedHours));
    }

    public static bool Qualifies(IReadOnlyDictionary<Guid, HashSet<Guid>> qualifications, Guid employeeId, Guid? laborCategoryId) =>
        laborCategoryId is { } c && qualifications.TryGetValue(employeeId, out var set) && set.Contains(c);

    public static StaffingGapStatus StatusOf(decimal budget, decimal left, decimal staffed, decimal tolerancePercent)
    {
        var gap = left - staffed;
        var tolerance = Math.Max(1m, left * tolerancePercent / 100m);
        return budget == 0 && staffed > 0 ? StaffingGapStatus.NotBudgeted
            : gap > tolerance ? StaffingGapStatus.Understaffed
            : gap < -tolerance ? StaffingGapStatus.Overstaffed
            : StaffingGapStatus.Balanced;
    }
}

/// <summary>The three setup steps (contract and WBS → budget by labor category → staff people), each with one plain sentence.</summary>
public static class SetupSteps
{
    public static ProjectSetupProgressDto Build(Guid projectId, string? contractNumber, int chargeCodes, IReadOnlyList<StaffingCategoryDto> categories)
    {
        var budgeted = categories.Where(c => c.BudgetHours > 0).ToList();
        var hasBudget = budgeted.Count > 0;
        var left = budgeted.Sum(c => c.LeftHours);
        var staffed = budgeted.Sum(c => Math.Min(c.StaffedHours, c.LeftHours));
        var unstaffed = budgeted.Where(c => c.Status == StaffingGapStatus.Understaffed).ToList();
        var assigned = categories.Sum(c => c.StaffedHours);

        // With no labor budget yet, the people assigned are the plan.
        var staffedDone = hasBudget ? unstaffed.Count == 0 : assigned > 0;
        string H(decimal h) => ProjectStaffingService.Hours(h);

        return new ProjectSetupProgressDto(projectId,
        [
            new ProjectSetupStepDto("wbs", "Plan the work",
                "Build the WBS on the Plan tab: type a task and press Enter, Tab makes it a subtask, and codes are numbered for you. The lowest rows are the charge codes people charge time to.",
                chargeCodes > 0,
                $"{(contractNumber is null ? "No contract (internal work)" : $"Contract {contractNumber}")} · {(chargeCodes == 1 ? "1 charge code" : $"{chargeCodes} charge codes")}",
                "plan", chargeCodes > 0 ? "Open the plan" : "Plan the work"),
            new ProjectSetupStepDto("budget", "Budget by labor category",
                "The contract is priced by labor category, so budget each WBS element in labor-category hours (for example, 1,200 Systems Engineer II hours on 1.2).",
                hasBudget,
                hasBudget
                    ? $"{H(budgeted.Sum(c => c.BudgetHours))} hours in {(budgeted.Count == 1 ? "1 labor category" : $"{budgeted.Count} labor categories")}"
                    : "No labor budget yet",
                "staffing/budget", hasBudget ? "Open the budget" : "Start the budget"),
            new ProjectSetupStepDto("staff", "Assign people",
                "Give each person their hours on a task and the dates. Workbench spreads the hours over the weeks and lets them charge the task.",
                staffedDone,
                !hasBudget ? (assigned > 0 ? $"{H(assigned)} hours assigned from this week on; they're the plan until there's a budget" : "Nobody assigned yet")
                    : staffedDone ? $"{H(staffed)} of {H(left)} hours left are staffed"
                    : $"{H(staffed)} of {H(left)} hours left are staffed; {H(unstaffed.Sum(c => c.Gap))} have nobody yet",
                "staffing/staff", "Assign people"),
        ]);
    }
}
