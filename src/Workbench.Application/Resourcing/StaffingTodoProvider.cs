using Workbench.Application.MyTodo;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>
/// A project manager's staffing to-dos for each open project they manage: budget it by labor category, staff the budgeted
/// hours nobody is on yet, and sort out staffing over the budget or people booked past their hours.
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

        var plans = new List<ProjectStaffingPlanDto>();
        foreach (var project in await context.ManagedProjectsAsync(cancellationToken))
        {
            plans.Add(await staffing.GetPlanAsync(project.Id, cancellationToken));
        }

        var unbudgeted = plans.Where(p => !p.HasLaborBudget).ToList();
        var items = plans.Where(p => p.HasLaborBudget || unbudgeted.Count == 1).SelectMany(For).ToList();
        if (unbudgeted.Count > 1)
        {
            // One item for all of them, so several new projects don't crowd out everything else.
            var codes = unbudgeted.Select(p => p.ProjectCode).ToList();
            items.Add(new MyTodoItem(
                $"staffing:budget:{string.Join(",", codes)}", TodoCategories.Project,
                $"Budget {codes.Count} projects by labor category",
                $"{Join(codes)} have no labor budget yet. Say how many hours of each labor category each WBS element may use, then staff people into those hours.",
                TodoUrgency.ThisWeek, null, $"projects/{unbudgeted[0].ProjectId}/budgets", $"Start with {codes[0]}", Priority: 5));
        }

        return items;
    }

    private static string Join(IReadOnlyList<string> codes) =>
        codes.Count <= 4 ? string.Join(", ", codes.Take(codes.Count - 1)) + " and " + codes[^1] : $"{string.Join(", ", codes.Take(4))} and {codes.Count - 4} more";

    public static IEnumerable<MyTodoItem> For(ProjectStaffingPlanDto plan)
    {
        var link = $"projects/{plan.ProjectId}/staffing";
        string H(decimal h) => ProjectStaffingService.Hours(h);
        if (!plan.HasLaborBudget)
        {
            yield return new MyTodoItem(
                $"staffing:{plan.ProjectId}:budget", TodoCategories.Project,
                $"Budget {plan.ProjectCode} by labor category",
                "Say how many hours of each labor category each WBS element may use. Then staff people into those hours.",
                TodoUrgency.ThisWeek, null, $"projects/{plan.ProjectId}/budgets", "Start the budget", Priority: 5);
            yield break;
        }

        var unstaffed = plan.Categories.Where(c => c.Status == StaffingGapStatus.Understaffed).ToList();
        if (unstaffed.Count > 0)
        {
            yield return new MyTodoItem(
                $"staffing:{plan.ProjectId}:unstaffed:{H(plan.UnstaffedHours)}", TodoCategories.Project,
                $"{H(plan.UnstaffedHours)} budgeted hours on {plan.ProjectCode} have nobody staffed",
                string.Join("; ", Describe(plan, unstaffed)) + ".",
                TodoUrgency.ThisWeek, null, link, "Staff people", Priority: 5);
        }

        var over = plan.Categories.Where(c => c.Status is StaffingGapStatus.Overstaffed or StaffingGapStatus.NotBudgeted).ToList();
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
