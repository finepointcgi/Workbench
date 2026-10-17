namespace Workbench.Contracts.Costpoint;

public enum CostpointSyncStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>One pull from Costpoint into the read-only mirror. Counts are rows read from the source.</summary>
public sealed record CostpointSyncRunDto(
    Guid Id,
    string Source,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    CostpointSyncStatus Status,
    int Projects,
    int WbsElements,
    int Employees,
    int LaborCategories,
    int Budgets,
    int Actuals,
    int Inserted,
    int Updated,
    int Deleted,
    int Skipped,
    string? Error);

/// <summary>
/// <paramref name="Source"/> is the configured adapter (<c>None</c>, <c>Csv</c>, <c>Rest</c>, <c>Database</c>).
/// The counts are what the mirror holds now.
/// </summary>
public sealed record CostpointStatusDto(
    string Source,
    bool Configured,
    CostpointSyncRunDto? LastRun,
    int Projects,
    int WbsElements,
    int Employees,
    int Actuals);

public sealed record CostpointProjectDto(string ProjectId, string Name, string? OrganizationId, DateOnly? Start, DateOnly? End, bool Active);

public sealed record CostpointWbsDto(string ProjectId, string WbsId, string? ParentWbsId, string Name, int Level, bool ChargeAllowed);

/// <summary>
/// Sheet plan against Costpoint for one WBS element (Costpoint figures include its descendant WBS elements).
/// Planned values sum the sheet's "Planned hours" and "Budget" columns over leaf items mapped to the WBS.
/// Variances are plan minus actual, so a negative value is an overrun.
/// </summary>
public sealed record PlanVsActualRow(
    string WbsId,
    string? WbsName,
    int ItemCount,
    decimal PlannedHours,
    decimal PlannedBudget,
    decimal BudgetHours,
    decimal BudgetAmount,
    decimal ActualHours,
    decimal ActualCost)
{
    public decimal HoursVariance => PlannedHours - ActualHours;

    public decimal CostVariance => PlannedBudget - ActualCost;

    /// <summary>Actual cost as a fraction of the planned budget, or null when nothing is planned.</summary>
    public decimal? Burn => PlannedBudget == 0 ? null : ActualCost / PlannedBudget;
}

/// <param name="Notes">Things the user should know, e.g. a missing "Planned hours" column or an unknown WBS.</param>
public sealed record PlanVsActualDto(
    Guid SheetId,
    string? ProjectId,
    string? ProjectName,
    IReadOnlyList<PlanVsActualRow> Rows,
    PlanVsActualRow Totals,
    DateTime? LastSyncedAtUtc,
    IReadOnlyList<string> Notes);

/// <summary>
/// Costpoint mirror: sync status and history, lookups and plan-vs-actual. Read-only with respect to
/// Costpoint; <see cref="SyncNowAsync"/> only refreshes Workbench's mirror.
/// </summary>
public interface ICostpointService
{
    Task<CostpointStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Most recent runs first.</summary>
    Task<IReadOnlyList<CostpointSyncRunDto>> ListSyncRunsAsync(int take = 20, CancellationToken cancellationToken = default);

    /// <summary>Runs a sync now and returns the finished run (which may have failed; see its status).</summary>
    Task<CostpointSyncRunDto> SyncNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Projects whose id or name contains <paramref name="query"/> (all when blank).</summary>
    Task<IReadOnlyList<CostpointProjectDto>> SearchProjectsAsync(string? query, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>WBS elements, optionally limited to one project, whose id or name contains <paramref name="query"/>.</summary>
    Task<IReadOnlyList<CostpointWbsDto>> SearchWbsAsync(string? projectId, string? query, int take = 20, CancellationToken cancellationToken = default);

    Task<PlanVsActualDto> GetPlanVsActualAsync(Guid sheetId, CancellationToken cancellationToken = default);
}
