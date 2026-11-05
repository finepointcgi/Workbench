using Workbench.Application.MyTodo;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// A project manager's staffing to-dos for each open project they manage: assign people when nobody is (without a labor
/// budget the people assigned are the plan), staff the budgeted hours nobody is on yet, and sort out staffing over the
/// budget or people booked past their hours.
/// </summary>
public sealed class StaffingTodoProvider : IMyTodoProvider
{
    public string Name => "Staffing";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IProjectStaffingService>() is not { } staffing)
        {
            return [];
        }

        // A project Finance handed over is planned from its plan to-do (ProjectPlanTodoProvider), not "assign people".
        var managed = await context.ManagedProjectsAsync(cancellationToken);
        var ids = managed.Select(p => p.Id).ToList();
        HashSet<Guid> handedOver, approved;
        await using (var db = await context.DbFactory.CreateAsync(cancellationToken))
        {
            approved = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.Set<Domain.ProjectAccounting.ProjectPlan>().Where(p => ids.Contains(p.ProjectId) && p.Status == Domain.ProjectAccounting.ProjectPlanStatus.Approved)
                    .Select(p => p.ProjectId), cancellationToken)).ToHashSet();
            handedOver = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.Set<Domain.ProjectAccounting.ProjectPlan>().Where(p => ids.Contains(p.ProjectId) && p.Status != Domain.ProjectAccounting.ProjectPlanStatus.Approved)
                    .Select(p => p.ProjectId), cancellationToken)).ToHashSet();
        }

        var plans = new List<ProjectStaffingPlanDto>();
        foreach (var project in managed.Where(p => !handedOver.Contains(p.Id)))
        {
            plans.Add(await staffing.GetPlanAsync(project.Id, cancellationToken));
        }

        var empty = plans.Where(NeedsPeople).ToList();
        var items = plans.SelectMany(p => For(p, includeAssign: empty.Count == 1, planIsBudget: approved.Contains(p.ProjectId))).ToList();
        if (empty.Count > 1)
        {
            // One item for all of them, so several new projects don't crowd out everything else.
            var codes = empty.Select(p => p.ProjectCode).ToList();
            items.Add(new MyTodoItem(
                $"staffing:assign:{string.Join(",", codes)}", TodoCategories.Project,
                $"Assign people to {codes.Count} projects",
                $"Nobody is assigned to {Join(codes)} yet. Give each person their hours on a task and the dates.",
                TodoUrgency.ThisWeek, null, $"projects/{empty[0].ProjectId}/plan", $"Start with {codes[0]}", Priority: 5));
        }

        return items;
    }

    private static string Join(IReadOnlyList<string> codes) =>
        codes.Count <= 4 ? string.Join(", ", codes.Take(codes.Count - 1)) + " and " + codes[^1] : $"{string.Join(", ", codes.Take(4))} and {codes.Count - 4} more";

    /// <summary>No labor budget and nobody staffed from this week on: the PM's next step is assigning people.</summary>
    private static bool NeedsPeople(ProjectStaffingPlanDto plan) => !plan.HasLaborBudget && plan.StaffedHours == 0;

    public static IEnumerable<MyTodoItem> For(ProjectStaffingPlanDto plan) => For(plan, includeAssign: true);

    /// <param name="planIsBudget">Finance approved the plan, so it is the budget: changes since are sent for approval (the plan's own to-do), not chased as staffing gaps.</param>
    private static IEnumerable<MyTodoItem> For(ProjectStaffingPlanDto plan, bool includeAssign, bool planIsBudget = false)
    {
        var link = $"projects/{plan.ProjectId}/plan";
        string H(decimal h) => ProjectStaffingService.Hours(h);
        if (NeedsPeople(plan) && includeAssign)
        {
            yield return new MyTodoItem(
                $"staffing:{plan.ProjectId}:assign", TodoCategories.Project,
                $"Assign people to {plan.ProjectCode}",
                "Nobody is on it yet. Give each person their hours on a task and the dates; that becomes the plan.",
                TodoUrgency.ThisWeek, null, link, "Assign people", Priority: 5);
        }

        // Without a labor budget there's nothing to be under or over; the people assigned are the plan.
        var unstaffed = plan.HasLaborBudget && !planIsBudget ? plan.Categories.Where(c => c.Status == StaffingGapStatus.Understaffed).ToList() : [];
        if (unstaffed.Count > 0)
        {
            yield return new MyTodoItem(
                $"staffing:{plan.ProjectId}:unstaffed:{H(plan.UnstaffedHours)}", TodoCategories.Project,
                $"{H(plan.UnstaffedHours)} budgeted hours on {plan.ProjectCode} have nobody staffed",
                string.Join("; ", Describe(plan, unstaffed)) + ".",
                TodoUrgency.ThisWeek, null, link, "Assign people", Priority: 5);
        }

        var over = plan.HasLaborBudget && !planIsBudget ? plan.Categories.Where(c => c.Status is StaffingGapStatus.Overstaffed or StaffingGapStatus.NotBudgeted).ToList() : [];
        if (over.Count > 0)
        {
            yield return new MyTodoItem(
                $"staffing:{plan.ProjectId}:over:{H(plan.OverstaffedHours)}", TodoCategories.Project,
                $"{plan.ProjectCode} is staffed {H(plan.OverstaffedHours)} hours over its budget",
                string.Join("; ", over.Select(c => c.Status == StaffingGapStatus.NotBudgeted
                    ? $"{c.LaborCategory}: {H(c.StaffedHours)} h staffed with no budget"
                    : $"{c.LaborCategory}: {H(c.StaffedHours)} h staffed, {H(c.LeftHours)} left in the budget"))
                    + ". Reduce the hours or raise the budget.",
                TodoUrgency.ThisWeek, null, link, "Review staffing", Priority: 5);
        }

        if (plan.Overbooked.Count > 0)
        {
            var names = plan.Overbooked.Select(p => p.Name).ToList();
            yield return new MyTodoItem(
                $"staffing:{plan.ProjectId}:overbooked:{string.Join(",", plan.Overbooked.Select(p => p.EmployeeId))}", TodoCategories.Project,
                names.Count == 1 ? $"{names[0]} is booked past their hours" : $"{names.Count} people on {plan.ProjectCode} are booked past their hours",
                $"{string.Join(", ", names.Take(4))}{(names.Count > 4 ? "…" : "")}: staffed for more hours than they work in at least one of the next {ProjectStaffingService.OverbookedWeeksAhead} weeks, across all their projects.",
                TodoUrgency.ThisWeek, null, link, "Review staffing", Priority: 4);
        }
    }

    /// <summary>"400 h of SE2 Systems Engineer II on 1002.01.02" for the biggest gaps (WBS elements when the gap is all on one).</summary>
    public static IEnumerable<string> Describe(ProjectStaffingPlanDto plan, IReadOnlyList<StaffingCategoryDto> categories) =>
        categories.Take(3).Select(c =>
        {
            var slots = plan.Slots.Where(s => s.LaborCategoryId == c.LaborCategoryId && s.WbsElementId is not null && s.Status == StaffingGapStatus.Understaffed).ToList();
            var where = slots.Count switch
            {
                0 => "",
                1 => $" on {slots[0].WbsCode}",
                _ => $" on {string.Join(", ", slots.Take(3).Select(s => s.WbsCode))}{(slots.Count > 3 ? "…" : "")}",
            };
            return $"{ProjectStaffingService.Hours(c.Gap)} h of {c.LaborCategory}{where}";
        });
}
