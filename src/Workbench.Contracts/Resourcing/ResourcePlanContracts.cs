namespace Workbench.Contracts.Resourcing;

// The resource-loaded plan: a project's plan tasks with the people assigned to them, their hours and what they cost, so a
// PM plans on one screen ("these tasks, these dates, these people, this many hours, this cost"). People are the same
// StaffingAssignment rows the Budget & staffing tab shows; nothing is stored twice. "Try changes" works out what moving
// dates or changing people would do before anything is saved, and applies them in one step with an undo.

/// <summary>
/// Someone on a task: their assignment (<see cref="AssignmentId"/> null for hours staffed week by week, which are changed
/// on the Budget &amp; staffing tab), labor category, dates and hours. <see cref="DirectCost"/> is hours × their cost rate
/// in each week; <see cref="BurdenedCost"/> adds fringe, overhead and G&amp;A. Both are null when the viewer may not see
/// cost. <see cref="Overbooked"/> lists weeks from this week on where they're booked past their hours on every project.
/// </summary>
public sealed record PlanPersonDto(
    Guid? AssignmentId,
    Guid EmployeeId,
    string Name,
    Guid? LaborCategoryId,
    string? LaborCategory,
    DateOnly From,
    DateOnly To,
    decimal Hours,
    decimal? DirectCost,
    decimal? BurdenedCost,
    IReadOnlyList<AssignmentOverbookedWeekDto> Overbooked,
    bool Changed = false)
{
    /// <summary>What the viewer may see: burdened when they may see burden, else direct.</summary>
    public decimal? Cost => BurdenedCost ?? DirectCost;

    public bool IsOverbooked => Overbooked.Count > 0;
}

/// <summary>
/// One plan task with its people. A summary task's <see cref="Hours"/> and cost add up its subtasks' and any people on it.
/// <see cref="WbsElementId"/> is the charge code made from the task; people can only be assigned to a task that has one.
/// </summary>
public sealed record PlanTaskRowDto(
    Guid ItemId,
    string OutlineNumber,
    string Title,
    int Depth,
    bool IsSummary,
    bool IsMilestone,
    Guid? WbsElementId,
    string? WbsCode,
    DateOnly? Start,
    DateOnly? Finish,
    int? TotalFloatDays,
    IReadOnlyList<PlanPersonDto> People,
    decimal Hours,
    decimal? DirectCost,
    decimal? BurdenedCost,
    bool Changed = false)
{
    public decimal? Cost => BurdenedCost ?? DirectCost;

    public bool CanAssign => WbsElementId is not null;

    /// <summary>Someone on this task (not its subtasks) is booked past their hours.</summary>
    public bool IsOverbooked => People.Any(p => p.IsOverbooked);
}

/// <summary>A labor category's planned hours and cost against its budget (0 budget hours: not budgeted).</summary>
public sealed record PlanCategoryTotalDto(
    Guid? LaborCategoryId,
    string LaborCategory,
    decimal Hours,
    decimal? DirectCost,
    decimal? BurdenedCost,
    decimal BudgetHours,
    decimal? BudgetAmount)
{
    public decimal? Cost => BurdenedCost ?? DirectCost;

    /// <summary>Budgeted hours nobody is planned for (negative: planned past the budget).</summary>
    public decimal HoursGap => BudgetHours - Hours;
}

/// <summary>
/// The plan's totals: hours and cost by labor category against the budget (or, with no labor budget, the plan is the
/// budget), when it starts and finishes, the critical path's float and who is overbooked.
/// <see cref="CriticalFloatDays"/> is the least total float of unfinished tasks in working days (0 = no room, negative = a
/// deadline is missed). <see cref="BudgetAmount"/> is the labor budget in dollars, compared with <see cref="DirectCost"/>.
/// </summary>
public sealed record PlanTotalsDto(
    decimal Hours,
    decimal? DirectCost,
    decimal? BurdenedCost,
    string? BudgetName,
    decimal BudgetHours,
    decimal? BudgetAmount,
    IReadOnlyList<PlanCategoryTotalDto> Categories,
    DateOnly? Start,
    DateOnly? Finish,
    int? CriticalFloatDays,
    IReadOnlyList<string> OverbookedPeople)
{
    public decimal? Cost => BurdenedCost ?? DirectCost;

    public bool HasBudget => BudgetHours > 0 || BudgetAmount > 0;

    /// <summary>With no labor budget, the people and hours on the plan are the budget.</summary>
    public bool PlanIsTheBudget => !HasBudget;

    public decimal HoursGap => BudgetHours - Hours;

    /// <summary>Budget left after the plan's direct labor (negative: over budget). Null without a budget or cost.</summary>
    public decimal? CostGap => HasBudget && BudgetAmount is { } budget && DirectCost is { } cost ? budget - cost : null;
}

/// <summary>The DCMA 14-point check in short: how many checks pass and fail (the rest can't be measured yet).</summary>
public sealed record PlanHealthSummaryDto(int Passed, int Failed, int NotMeasured);

/// <summary>
/// A project's plan with its people. <see cref="ShowsCost"/> is false for viewers who may not see labor cost (costs are
/// then null); <see cref="CostIncludesBurden"/> says whether costs include fringe, overhead and G&amp;A.
/// <see cref="CanChange"/> is true for those who may assign people; <see cref="CanMoveTasks"/> for those who may also edit
/// the plan. <see cref="NotOnATask"/> are people on the whole project or on a charge code that isn't a task on the plan.
/// </summary>
public sealed record ResourcePlanDto(
    Guid ProjectId,
    string ProjectCode,
    Guid SheetId,
    DateOnly AsOf,
    bool ShowsCost,
    bool CostIncludesBurden,
    bool CanChange,
    bool CanMoveTasks,
    IReadOnlyList<PlanTaskRowDto> Tasks,
    IReadOnlyList<PlanPersonDto> NotOnATask,
    PlanTotalsDto Totals,
    PlanHealthSummaryDto? Health,
    IReadOnlyList<string> Notes)
{
    public PlanTaskRowDto? Task(Guid itemId) => Tasks.FirstOrDefault(t => t.ItemId == itemId);

    public int OverbookedTasks => Tasks.Count(t => t.IsOverbooked);
}

public enum PlanChangeKind
{
    /// <summary>
    /// Move a task. With <see cref="PlanChange.Start"/> only it keeps its working days; with <see cref="PlanChange.Finish"/>
    /// it ends then (both: exactly those dates). Successors move with it, and so do the people on every task that moves.
    /// </summary>
    MoveTask,

    /// <summary>
    /// Assign someone (no <see cref="PlanChange.AssignmentId"/>) to the task <see cref="PlanChange.ItemId"/> (or the whole
    /// project without one), or change an assignment: the person, hours, labor category or dates. Anything left out stays.
    /// </summary>
    SetPerson,

    /// <summary>Take someone off: removes the assignment <see cref="PlanChange.AssignmentId"/>.</summary>
    RemovePerson,
}

/// <summary>
/// One change to try or make. New people's dates default to the task's (after any moves), from today on.
/// <see cref="Weeks"/> puts an assignment's weeks back exactly (undo uses it, so weeks adjusted by hand come back too).
/// <see cref="WbsElementId"/> places a new person on a charge code that isn't a task on the plan.
/// </summary>
public sealed record PlanChange(
    PlanChangeKind Kind,
    Guid? ItemId = null,
    DateOnly? Start = null,
    DateOnly? Finish = null,
    Guid? AssignmentId = null,
    Guid? EmployeeId = null,
    decimal? Hours = null,
    Guid? LaborCategoryId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyList<AssignmentWeekDto>? Weeks = null,
    Guid? WbsElementId = null);

/// <summary>
/// What changes would do, worked out without saving anything: the totals before and after, the plan after (rows and people
/// marked <c>Changed</c>), and the effect in plain sentences. <see cref="FinishMovesDays"/> is how many working days the
/// plan's finish moves (positive = later). <see cref="Errors"/> are changes that can't be made as asked.
/// </summary>
public sealed record PlanTryDto(
    PlanTotalsDto Before,
    PlanTotalsDto After,
    int? FinishMovesDays,
    IReadOnlyList<PlanTaskRowDto> Tasks,
    IReadOnlyList<PlanPersonDto> NotOnATask,
    IReadOnlyList<string> Errors)
{
    public bool CanApply => Errors.Count == 0;

    /// <summary>The effect in plain sentences, from the totals (so cost shows only to those who may see it).</summary>
    public IReadOnlyList<string> Effects => PlanEffects.Describe(Before, After, FinishMovesDays);
}

/// <summary>Plain sentences on what changes do to the finish, the hours, the cost, the budget and who is overbooked.</summary>
public static class PlanEffects
{
    public static IReadOnlyList<string> Describe(PlanTotalsDto before, PlanTotalsDto after, int? finishMovesDays)
    {
        var effects = new List<string>();
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string Date(DateOnly d) => d.ToString("MMM d, yyyy", c);
        string Hours(decimal h) => h.ToString("#,##0.##", c);
        string Money(decimal m) => (m < 0 ? "−$" : "$") + Math.Abs(m).ToString("#,##0", c);
        string Days(int d) => Math.Abs(d) == 1 ? "1 working day" : $"{Math.Abs(d)} working days";

        if (after.Finish is { } finish)
        {
            effects.Add(before.Finish is { } was && was != finish
                ? $"The plan finishes {Date(finish)} instead of {Date(was)}" + (finishMovesDays is { } m && m != 0 ? $" ({Days(m)} {(m > 0 ? "later" : "earlier")})." : ".")
                : $"The finish doesn't move ({Date(finish)}).");
        }

        if (after.Hours != before.Hours)
        {
            effects.Add($"Planned hours go from {Hours(before.Hours)} to {Hours(after.Hours)}.");
        }

        if (after.Cost is { } cost && before.Cost is { } wasCost && cost != wasCost)
        {
            var delta = cost - wasCost;
            effects.Add($"Labor cost goes from {Money(wasCost)} to {Money(cost)} ({(delta > 0 ? "+" : "")}{Money(delta)}).");
        }

        if (after.CostGap is { } gap)
        {
            var wasText = before.CostGap is { } w && w != gap ? $" (was {(w >= 0 ? $"{Money(w)} left" : $"{Money(-w)} over")})." : ".";
            effects.Add(gap >= 0 ? $"That leaves {Money(gap)} of the labor budget{wasText}" : $"That's {Money(-gap)} over the labor budget{wasText}");
        }
        else if (after.HasBudget && after.HoursGap != before.HoursGap)
        {
            effects.Add(after.HoursGap >= 0 ? $"{Hours(after.HoursGap)} budgeted hours have nobody planned." : $"Planned {Hours(-after.HoursGap)} hours past the budget.");
        }
        else if (after.PlanIsTheBudget && after.Hours != before.Hours)
        {
            effects.Add("There's no labor budget, so the plan is the budget.");
        }

        var nowOver = after.OverbookedPeople.Except(before.OverbookedPeople).ToList();
        var fixedOver = before.OverbookedPeople.Except(after.OverbookedPeople).ToList();
        if (nowOver.Count > 0)
        {
            effects.Add($"{string.Join(", ", nowOver)} would be booked past their hours.");
        }

        if (fixedOver.Count > 0)
        {
            effects.Add($"{string.Join(", ", fixedOver)} would no longer be booked past their hours.");
        }

        if (nowOver.Count == 0 && fixedOver.Count == 0)
        {
            effects.Add(after.OverbookedPeople.Count == 0 ? "Nobody is booked past their hours." : $"Still booked past their hours: {string.Join(", ", after.OverbookedPeople)}.");
        }

        return effects;
    }
}

/// <summary>Changes made in one step: what changed, and the changes that put everything back as it was.</summary>
public sealed record PlanAppliedDto(int TasksMoved, int PeopleChanged, string Summary, IReadOnlyList<PlanChange> Undo);

public enum OverbookingFixKind
{
    /// <summary>The same hours over more weeks, so each week fits around their other work.</summary>
    SpreadOverMoreWeeks,

    /// <summary>Move the task (and its successors) to start once the person has room.</summary>
    MoveAfterOtherWork,

    /// <summary>Give the hours to someone in the same labor category who has room.</summary>
    SomeoneElse,
}

/// <summary>A one-click fix for an overbooked person: what it does in a sentence, and the changes that do it.</summary>
public sealed record OverbookingFixDto(OverbookingFixKind Kind, string Label, string Explanation, IReadOnlyList<PlanChange> Changes);

/// <summary>HTTP body for trying or applying changes.</summary>
public sealed record PlanChangesRequest(IReadOnlyList<PlanChange> Changes);

/// <summary>
/// The plan, the budget and the people on one screen. Reading: whoever may see the project's staffing (Finance,
/// administrators, resource managers, the project's manager); cost follows the project financials rule and burden the
/// burdened-cost rule. Changing: those who may change its staffing, and editors of the plan when tasks move.
/// </summary>
public interface IResourcePlanService
{
    Task<ResourcePlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>What the changes would do. Saves nothing.</summary>
    Task<PlanTryDto> TryAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default);

    /// <summary>Makes the changes all at once (all or nothing) and returns the changes that undo them.</summary>
    Task<PlanAppliedDto> ApplyAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default);

    /// <summary>Ways to stop an assignment's person being booked past their hours (none when they aren't). Saves nothing.</summary>
    Task<IReadOnlyList<OverbookingFixDto>> ListFixesAsync(Guid projectId, Guid assignmentId, CancellationToken cancellationToken = default);
}
