namespace Workbench.Contracts.Resourcing;

// Staffing: how a project's labor budget (hours by labor category on each WBS element) is filled with named people.
// The GovCon order is budget by labor category first, then staff people into those categories; timesheets then charge
// actual hours by person, which roll up to the labor category for billing and for comparing with the budget.

/// <summary>Someone staffed on a slot: their hours from this week on, and whether they qualify for the slot's labor category.</summary>
public sealed record StaffedPersonDto(Guid EmployeeId, string Name, decimal Hours, bool Qualifies, DateOnly? Through);

/// <summary>
/// One WBS element × labor category on a project: the budgeted hours, hours charged so far, what's left, and the hours
/// staffed from this week on. <see cref="WbsElementId"/> is null for people staffed on the whole project rather than a
/// WBS element. <see cref="Gap"/> = left − staffed: positive is hours nobody is staffed on, negative is staffed over budget.
/// </summary>
public sealed record StaffingSlotDto(
    Guid? WbsElementId,
    string? WbsCode,
    string? WbsName,
    Guid? LaborCategoryId,
    string LaborCategory,
    decimal BudgetHours,
    decimal ChargedHours,
    decimal LeftHours,
    decimal StaffedHours,
    decimal Gap,
    StaffingGapStatus Status,
    IReadOnlyList<StaffedPersonDto> People)
{
    /// <summary>"1002.01.02 Build" or "Whole project".</summary>
    public string WbsLabel => WbsCode is null ? "Whole project" : $"{WbsCode} {WbsName}".Trim();
}

/// <summary>A labor category's totals across the project (every WBS element, plus people staffed on the whole project).</summary>
public sealed record StaffingCategoryDto(
    Guid? LaborCategoryId,
    string LaborCategory,
    decimal BudgetHours,
    decimal ChargedHours,
    decimal LeftHours,
    decimal StaffedHours,
    decimal Gap,
    StaffingGapStatus Status);

/// <summary>Someone staffed on this project who is booked past their capacity in at least one week (across all projects).</summary>
public sealed record OverbookedPersonDto(Guid EmployeeId, string Name, int OverWeeks, DateOnly FirstWeek, decimal WorstAllocated, decimal WorstCapacity);

/// <summary>
/// A project's budget against its staffing. Staffed hours count from this week on and are compared with what's left of
/// the budget (budget − approved hours charged), so past weeks don't count twice.
/// </summary>
public sealed record ProjectStaffingPlanDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string? BudgetName,
    DateOnly AsOf,
    IReadOnlyList<StaffingCategoryDto> Categories,
    IReadOnlyList<StaffingSlotDto> Slots,
    IReadOnlyList<OverbookedPersonDto> Overbooked,
    IReadOnlyList<string> Notes)
{
    public bool HasLaborBudget => Categories.Any(c => c.BudgetHours > 0);

    /// <summary>Budgeted hours still to do that nobody is staffed on, by labor category (people staffed on the whole project count).</summary>
    public decimal UnstaffedHours => Categories.Where(c => c.Status == StaffingGapStatus.Understaffed).Sum(c => c.Gap);

    /// <summary>Hours staffed beyond what's left of the budget, or on labor categories with no budget.</summary>
    public decimal OverstaffedHours => Categories.Where(c => c.Status is StaffingGapStatus.Overstaffed or StaffingGapStatus.NotBudgeted).Sum(c => -c.Gap);

    public decimal LeftHours => Categories.Sum(c => c.LeftHours);

    public decimal StaffedHours => Categories.Sum(c => c.StaffedHours);
}

/// <summary>One step of setting up a project: contract and WBS → budget by labor category → staff people.</summary>
public sealed record ProjectSetupStepDto(
    string Key,
    string Title,

    /// <summary>One plain sentence on what the step is for.</summary>
    string Explanation,
    bool IsDone,

    /// <summary>Where the step stands, e.g. "640 of 1,000 budgeted hours staffed".</summary>
    string Status,

    /// <summary>The project page tab where the step is done ("setup", "budgets", "staffing").</summary>
    string Tab,
    string ActionLabel);

public sealed record ProjectSetupProgressDto(Guid ProjectId, IReadOnlyList<ProjectSetupStepDto> Steps)
{
    public int DoneCount => Steps.Count(s => s.IsDone);

    public bool IsComplete => Steps.All(s => s.IsDone);

    /// <summary>The first step that isn't done.</summary>
    public ProjectSetupStepDto? Next => Steps.FirstOrDefault(s => !s.IsDone);
}

public enum StaffingWarningKind
{
    /// <summary>The person's labor category is a different one, and nothing else qualifies them for this one.</summary>
    NotQualified,

    /// <summary>The person has no labor category, so their hours can't count against the budget.</summary>
    NoLaborCategory,

    /// <summary>This would staff more hours than are left in the budget for the WBS element and labor category.</summary>
    OverBudget,

    /// <summary>There's no budget for this labor category on this WBS element.</summary>
    NotBudgeted,

    /// <summary>The labor category has no bill rate on the project's contract, so the hours can't be billed at it.</summary>
    NotOnContract,
}

public sealed record StaffingWarningDto(StaffingWarningKind Kind, string Message);

/// <summary>A proposed staffing: <see cref="HoursPerWeek"/> for <see cref="Weeks"/> weeks from <see cref="WeekStart"/>.</summary>
public sealed record StaffingCheckRequest(
    Guid ProjectId,
    Guid EmployeeId,
    Guid? WbsElementId,
    Guid? LaborCategoryId,
    DateOnly WeekStart,
    decimal HoursPerWeek,
    int Weeks = 1);

/// <summary>
/// The result of checking a proposed staffing. <see cref="LaborCategoryId"/> is the category the hours will count in (the
/// one asked for, else the person's own). Warnings don't block saving; they explain what the PM is about to do.
/// </summary>
public sealed record StaffingCheckDto(
    Guid? LaborCategoryId,
    string? LaborCategory,
    bool Qualifies,
    decimal LeftHours,
    decimal StaffedHoursAfter,
    IReadOnlyList<StaffingWarningDto> Warnings);

/// <summary>Someone who could be staffed: their own labor category, whether they qualify for the one asked about, and how booked they are.</summary>
public sealed record StaffingCandidateDto(
    Guid EmployeeId,
    string Name,
    Guid? LaborCategoryId,
    string? LaborCategory,
    bool Qualifies,
    decimal StandardHoursPerWeek,

    /// <summary>Average hours a week they're already staffed (all projects) over the next four weeks.</summary>
    decimal BookedHoursPerWeek,

    /// <summary>What they can do, best first, so the person assigning can see who fits the work.</summary>
    IReadOnlyList<EmployeeSkillDto>? Skills = null);

/// <summary>
/// Staffing a project against its labor budget. Finance, administrators, resource managers and the project's manager.
/// People are staffed with <see cref="IResourceAllocationService.CreateAsync"/>; this service compares and checks.
/// </summary>
public interface IProjectStaffingService
{
    Task<ProjectStaffingPlanDto> GetPlanAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectSetupProgressDto> GetSetupProgressAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<StaffingCheckDto> CheckAsync(StaffingCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>Active people, those who qualify for <paramref name="laborCategoryId"/> first.</summary>
    Task<IReadOnlyList<StaffingCandidateDto>> ListCandidatesAsync(Guid projectId, Guid? laborCategoryId, CancellationToken cancellationToken = default);
}
