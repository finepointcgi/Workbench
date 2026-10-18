using Workbench.Contracts.Financials;

namespace Workbench.Contracts.Resourcing;

// AllocationOrigin mirrors Workbench.Domain.Resourcing.AllocationSource by name and value.

public enum AllocationOrigin { Manual, Plan }

/// <summary>Planned hours for one person on one project (optionally a WBS element) in the week starting <see cref="WeekStart"/> (a Monday).</summary>
public sealed record AllocationDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid? WbsElementId,
    string? WbsCode,
    Guid? LaborCategoryId,
    string? LaborCategoryCode,
    DateOnly WeekStart,
    decimal Hours,
    decimal? Percent,
    AllocationOrigin Source,
    string? Notes);

/// <summary>
/// Creates allocations for <see cref="Weeks"/> consecutive weeks from <see cref="WeekStart"/> (moved back to its Monday).
/// Give <see cref="Hours"/> per week, or <see cref="Percent"/> of the person's standard week.
/// <see cref="LaborCategoryId"/> defaults to the person's labor category.
/// </summary>
public sealed record SaveAllocationRequest(
    Guid EmployeeId,
    Guid ProjectId,
    DateOnly WeekStart,
    decimal? Hours = null,
    decimal? Percent = null,
    Guid? WbsElementId = null,
    Guid? LaborCategoryId = null,
    int Weeks = 1,
    string? Notes = null);

public sealed record AllocationQuery(DateOnly From, DateOnly To, Guid? ProjectId = null, Guid? EmployeeId = null);

/// <summary>Generate from plan: weeks before <see cref="From"/> (default: this week) are left alone.</summary>
public sealed record GenerateAllocationsRequest(DateOnly? From = null, DateOnly? To = null);

public sealed record GenerateAllocationsResultDto(int Created, int Replaced, decimal TotalHours, IReadOnlyList<string> Skipped);

/// <summary>
/// Allocations. Project managers manage their own projects' allocations; resource managers and administrators manage
/// everyone's; Finance reads all; everyone sees their own.
/// </summary>
public interface IResourceAllocationService
{
    Task<IReadOnlyList<AllocationDto>> ListAsync(AllocationQuery query, CancellationToken cancellationToken = default);

    /// <summary>The signed-in employee's allocations.</summary>
    Task<IReadOnlyList<AllocationDto>> ListMineAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AllocationDto>> CreateAsync(SaveAllocationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes one week's allocation (Weeks is ignored).</summary>
    Task<AllocationDto> UpdateAsync(Guid allocationId, SaveAllocationRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid allocationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the project's plan-generated allocations from its plan sheet: each leaf task with dates and a Person
    /// assignment spreads its "Planned hours" (or the person's standard day when there are none) over its working days.
    /// </summary>
    Task<GenerateAllocationsResultDto> GenerateFromPlanAsync(Guid projectId, GenerateAllocationsRequest? request = null, CancellationToken cancellationToken = default);

    /// <summary>True when the signed-in user may create allocations on the project.</summary>
    Task<bool> CanManageAsync(Guid projectId, CancellationToken cancellationToken = default);
}

// ---- Capacity, heat map, staffing, utilization, demand ----

/// <summary>Capacity = standard hours − holidays − approved leave − the non-billable allowance.</summary>
public sealed record CapacityWeekDto(
    Guid EmployeeId,
    DateOnly WeekStart,
    decimal StandardHours,
    decimal HolidayHours,
    decimal LeaveHours,
    decimal NonBillableHours,
    decimal Capacity);

public enum LoadStatus { None, Under, Full, Over }

public sealed record HeatMapCellDto(DateOnly WeekStart, decimal Capacity, decimal Allocated, LoadStatus Status, decimal HolidayHours, decimal LeaveHours);

public sealed record HeatMapRowDto(
    Guid EmployeeId,
    string EmployeeName,
    string? OrgUnit,
    string? LaborCategory,
    IReadOnlyList<HeatMapCellDto> Weeks)
{
    public int OverWeeks => Weeks.Count(w => w.Status == LoadStatus.Over);
}

public sealed record HeatMapDto(IReadOnlyList<DateOnly> Weeks, IReadOnlyList<HeatMapRowDto> Rows, decimal FullThresholdPercent);

public sealed record HeatMapQuery(DateOnly From, int Weeks = 8, Guid? OrgUnitId = null, Guid? ProjectId = null);

public enum StaffingGapStatus { Balanced, Understaffed, Overstaffed, NotBudgeted }

/// <summary>One labor category on a project: remaining budget hours against hours allocated from this week on.</summary>
public sealed record StaffingRowDto(
    Guid? LaborCategoryId,
    string LaborCategory,
    decimal BudgetHours,
    decimal ActualHours,
    decimal RemainingBudgetHours,
    decimal AllocatedHours,

    /// <summary>Remaining budget − allocated: positive means hours nobody is allocated to yet.</summary>
    decimal Gap,
    StaffingGapStatus Status,
    IReadOnlyList<string> People);

public sealed record ProjectStaffingDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string? BudgetVersionName,
    DateOnly AsOf,
    IReadOnlyList<StaffingRowDto> Rows,
    decimal RemainingBudgetHours,
    decimal AllocatedHours,
    IReadOnlyList<string> Notes);

public enum UtilizationGrouping { Person, OrgUnit, Month }

/// <summary>Approved hours against available hours (standard − holidays − leave). Direct = hours on direct charge codes.</summary>
public sealed record UtilizationRowDto(
    string Key,
    string Name,
    decimal AvailableHours,
    decimal ActualHours,
    decimal DirectHours,

    /// <summary>DirectHours / AvailableHours (0.85 = 85%); null without available hours.</summary>
    decimal? DirectUtilization,
    decimal? TotalUtilization);

public sealed record UtilizationDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<UtilizationRowDto> ByPerson,
    IReadOnlyList<UtilizationRowDto> ByOrgUnit,
    IReadOnlyList<UtilizationRowDto> ByMonth,
    UtilizationRowDto Total,

    /// <summary>The target direct utilization (1 − the non-billable allowance).</summary>
    decimal TargetUtilization);

/// <summary>Budgeted hours in a future fiscal period that no allocation covers yet.</summary>
public sealed record OpenDemandDto(
    Guid ProjectId,
    string ProjectCode,
    Guid? LaborCategoryId,
    string LaborCategory,
    DateOnly PeriodStart,
    string PeriodName,
    decimal BudgetHours,
    decimal AllocatedHours,
    decimal OpenHours);

public sealed record FindPeopleQuery(DateOnly From, DateOnly To, Guid? LaborCategoryId = null, decimal MinAvailableHours = 1);

public sealed record AvailableWeekDto(DateOnly WeekStart, decimal Capacity, decimal Allocated, decimal Available);

public sealed record AvailablePersonDto(
    Guid EmployeeId,
    string EmployeeName,
    string? LaborCategory,
    string? OrgUnit,
    decimal CapacityHours,
    decimal AllocatedHours,
    decimal AvailableHours,
    IReadOnlyList<AvailableWeekDto> Weeks);

/// <summary>Capacity and load views over allocations, budgets and approved time.</summary>
public interface IResourcePlanningService
{
    Task<IReadOnlyList<CapacityWeekDto>> GetCapacityAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds = null, CancellationToken cancellationToken = default);

    /// <summary>People × weeks, allocated vs capacity. Employees without a staffing role see only their own row.</summary>
    Task<HeatMapDto> GetHeatMapAsync(HeatMapQuery query, CancellationToken cancellationToken = default);

    Task<ProjectStaffingDto> GetProjectStaffingAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<UtilizationDto> GetUtilizationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenDemandDto>> ListOpenDemandAsync(Guid? projectId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailablePersonDto>> FindAvailablePeopleAsync(FindPeopleQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Allocation-based ETC: remaining work priced from future allocations (hours × each person's cost rate) plus remaining
/// ODC budget, shown alongside the remaining-budget ETC in project financials. Finance, Admin or the project's manager.
/// </summary>
public interface IAllocationForecastService
{
    Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default);
}

/// <summary>HTTP body for <see cref="IResourcePlanningService.GetCapacityAsync"/>.</summary>
public sealed record CapacityRequest(DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? EmployeeIds = null);
