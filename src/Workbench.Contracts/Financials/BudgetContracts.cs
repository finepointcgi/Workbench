namespace Workbench.Contracts.Financials;

// Enums mirror Workbench.Domain.ProjectAccounting (BudgetKind, BudgetCostType, OdcType) by name and value.

public enum BudgetVersionKind { Baseline, Current, Forecast }

public enum BudgetCostKind { Labor, Odc }

public enum BudgetOdcType { Travel, Materials, Equipment, Subcontract, Consultant, Other }

/// <summary>Where a new budget version's lines come from.</summary>
public enum BudgetVersionSource
{
    /// <summary>No lines.</summary>
    Empty,

    /// <summary>A copy of <see cref="CreateBudgetVersionRequest.SourceVersionId"/>'s lines.</summary>
    CopyVersion,

    /// <summary>The "Planned hours" and "Budget" columns of the plan item linked to each WBS element.</summary>
    PlanSheet,
}

public sealed record BudgetVersionDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    BudgetVersionKind Kind,
    bool IsLocked,
    string? Description,
    DateTime CreatedAtUtc,
    string CreatedBy,
    DateTime? LockedAtUtc,
    string? LockedBy,
    Guid? SourceVersionId,
    decimal TotalHours,
    decimal TotalAmount,
    int LineCount);

/// <summary>
/// One budget amount. Labor lines: hours on a WBS leaf × labor category, Amount = Hours × PlanningRate.
/// ODC lines: dollars on a WBS element × ODC type. <see cref="PeriodStart"/> is the first day of a fiscal period, or null (unphased).
/// </summary>
public sealed record BudgetLineDto(
    Guid Id,
    Guid WbsElementId,
    string WbsCode,
    string WbsName,
    BudgetCostKind CostType,
    Guid? LaborCategoryId,
    string? LaborCategoryCode,
    BudgetOdcType? OdcType,
    DateOnly? PeriodStart,
    decimal Hours,
    decimal PlanningRate,
    decimal Amount);

public sealed record BudgetVersionDetailDto(BudgetVersionDto Version, IReadOnlyList<BudgetLineDto> Lines);

public sealed record CreateBudgetVersionRequest(
    string Name,
    BudgetVersionKind Kind,
    BudgetVersionSource Source = BudgetVersionSource.Empty,
    Guid? SourceVersionId = null,
    string? Description = null);

/// <summary>
/// A line to save. For labor lines <see cref="Amount"/> is ignored: the server prices them (keeping the stored
/// planning rate of an unchanged WBS × category × period line, otherwise the current planning rate).
/// </summary>
public sealed record SaveBudgetLineRequest(
    Guid WbsElementId,
    BudgetCostKind CostType,
    Guid? LaborCategoryId,
    BudgetOdcType? OdcType,
    DateOnly? PeriodStart,
    decimal Hours,
    decimal Amount = 0);

public sealed record LockBudgetRequest(string? Reason = null);

/// <summary>
/// Budget versions per project: at most one Baseline (locked once approved), one Current and any number of Forecasts.
/// Every change is audited.
/// </summary>
public interface IBudgetService
{
    /// <summary>Versions of a project: Baseline, Current, then Forecasts newest first.</summary>
    Task<IReadOnlyList<BudgetVersionDto>> ListVersionsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BudgetVersionDetailDto?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    Task<BudgetVersionDetailDto> CreateVersionAsync(Guid projectId, CreateBudgetVersionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces all lines of an unlocked version.</summary>
    Task<BudgetVersionDetailDto> SaveLinesAsync(Guid versionId, IReadOnlyList<SaveBudgetLineRequest> lines, CancellationToken cancellationToken = default);

    /// <summary>Locks a Baseline. A locked version can't be edited, repriced or promoted.</summary>
    Task<BudgetVersionDto> LockBaselineAsync(Guid versionId, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Makes a Forecast the project's Current version; the previous Current becomes a Forecast.</summary>
    Task<BudgetVersionDto> PromoteToCurrentAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Recalculates every labor line's planning rate and amount from today's cost rates.</summary>
    Task<BudgetVersionDetailDto> RepriceAsync(Guid versionId, CancellationToken cancellationToken = default);
}
