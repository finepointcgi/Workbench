using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Contracts.Financials;

/// <summary>
/// Budget, actual and estimate-at-completion figures for a project or a WBS element (rolled up its subtree).
/// Computed on request, never stored. Actual cost = approved hours × cost rate on the work date + approved, allowable ODCs.
/// </summary>
public sealed record FinancialFigures(
    decimal BudgetHours,
    decimal BudgetAmount,
    decimal ActualHours,
    decimal ActualLaborCost,
    decimal ActualOdcCost,
    decimal ActualCost,

    /// <summary>ActualCost / BudgetAmount (0.5 = 50%); null when there's no budget.</summary>
    decimal? PercentSpent,
    decimal RemainingBudget,
    decimal EtcHours,
    decimal EtcAmount,
    decimal EacHours,
    decimal EacAmount,

    /// <summary>Budget − EAC; negative means a projected overrun.</summary>
    decimal VacHours,
    decimal VacAmount,

    /// <summary>Approved hours × contract bill rate on the work date (meaningful for T&amp;M / labor-hour contracts).</summary>
    decimal BillableValue)
{
    public static FinancialFigures Zero { get; } = new(0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record ProjectFinancialSummaryDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid? ContractId,
    string? ContractNumber,
    ContractKind? ContractType,
    Guid? BudgetVersionId,
    string? BudgetVersionName,
    Guid? ForecastVersionId,
    string? ForecastVersionName,

    /// <summary>True for T&amp;M and labor-hour contracts, where <see cref="FinancialFigures.BillableValue"/> is shown.</summary>
    bool ShowsBillableValue,
    DateOnly AsOf,
    FinancialFigures Totals,
    ContractFundingDto? Funding,
    IReadOnlyList<string> Notes);

/// <summary>One WBS element with figures rolled up from its descendants. Rows come in tree order (parent before children).</summary>
public sealed record WbsFinancialRowDto(
    Guid WbsElementId,
    Guid? ParentId,
    string Code,
    string Name,
    int Depth,
    bool IsLeaf,
    FinancialFigures Figures);

public sealed record TimePhasedPointDto(
    Guid FiscalPeriodId,
    string PeriodName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal BudgetAmount,
    decimal ActualHours,
    decimal ActualLaborCost,
    decimal ActualOdcCost,
    decimal ActualCost,
    decimal CumulativeBudget,

    /// <summary>Null for periods that start after the as-of date.</summary>
    decimal? CumulativeActual,
    bool IsFuture);

public sealed record TimePhasedBurnDto(
    Guid ProjectId,
    Guid? BudgetVersionId,
    IReadOnlyList<TimePhasedPointDto> Periods,

    /// <summary>Budget on lines with no period; not in the per-period series.</summary>
    decimal UnphasedBudgetAmount,
    IReadOnlyList<string> Notes);

public enum FundingAlertLevel { Info, Warning, Critical }

/// <summary>A funding alert. <see cref="ThresholdPercent"/> is 75 or 90 for the funded-value thresholds, null otherwise.</summary>
public sealed record FundingAlertDto(FundingAlertLevel Level, int? ThresholdPercent, string Message);

/// <summary>
/// Funding status across all projects on a contract. <see cref="Spent"/> is actual cost (labor at cost plus allowable ODCs);
/// for T&amp;M and labor-hour contracts it's billable value plus allowable ODCs.
/// </summary>
public sealed record ContractFundingDto(
    Guid ContractId,
    string ContractNumber,
    string ContractName,
    ContractKind Type,
    DateOnly PeriodEnd,
    int ProjectCount,
    decimal CeilingValue,
    decimal FundedValue,
    decimal Spent,
    decimal FundedRemaining,
    decimal CeilingRemaining,

    /// <summary>Spent / FundedValue; null when nothing is funded.</summary>
    decimal? PercentOfFundedSpent,

    /// <summary>Average spend per fiscal period over the last 3 periods (the current one up to the as-of date).</summary>
    decimal AverageBurnPerPeriod,

    /// <summary>When funded value runs out at the average burn; null when there's no burn or it's already exhausted.</summary>
    DateOnly? FundingExhaustionDate,
    DateOnly AsOf,
    IReadOnlyList<FundingAlertDto> Alerts);

public interface IProjectFinancialsService
{
    /// <summary>
    /// Project totals. <paramref name="budgetVersionId"/> defaults to the Current version (or the Baseline when there's no Current);
    /// with <paramref name="forecastVersionId"/>, ETC comes from that Forecast's remaining amounts instead of the remaining budget.
    /// </summary>
    Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default);

    /// <summary>Every WBS element of the project with rolled-up figures, in tree order.</summary>
    Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default);

    /// <summary>Actual cost per fiscal period against the phased budget, with cumulative series.</summary>
    Task<TimePhasedBurnDto> GetTimePhasedBurnAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default);

    Task<ContractFundingDto> GetContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>Funding status of every active contract, most at-risk first.</summary>
    Task<IReadOnlyList<ContractFundingDto>> ListContractFundingAsync(CancellationToken cancellationToken = default);
}

public sealed record FinancialExportFile(string FileName, string ContentType, string Content);

public interface IFinancialExportService
{
    Task<FinancialExportFile> ExportWbsCsvAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default);

    Task<FinancialExportFile> ExportTimePhasedCsvAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default);
}
