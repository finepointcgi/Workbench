namespace Workbench.Contracts.Evm;

// Enums mirror Workbench.Domain.Evm by name and value.

/// <summary>How a work package earns its budget (BCWP).</summary>
public enum EvTechnique { ZeroHundred, FiftyFifty, PercentComplete, Milestones, LevelOfEffort, Apportioned }

public enum EvmBudgetSourceKind { ManagementReserve, UndistributedBudget, ContractChange }

public enum EvmChangeStatus { Pending, Approved, Rejected }

/// <summary>
/// Closed: frozen at its close. Reporting: the period containing today, statused live. Unstatused: an earlier period that
/// was never closed (its BCWP isn't known). Future: hasn't started.
/// </summary>
public enum EvmPeriodState { Closed, Reporting, Unstatused, Future }

public sealed record EvmMilestoneDto(
    Guid Id,
    string Name,
    decimal Weight,
    int Order,
    Guid? ItemId,
    DateOnly? PlannedDate,
    DateOnly? CompletedOn,
    bool IsComplete);

public sealed record EvmWorkPackageDto(
    Guid Id,
    Guid WbsElementId,
    string WbsCode,
    string WbsName,
    Guid ControlAccountWbsId,
    string ControlAccountCode,
    EvTechnique Technique,
    decimal Budget,
    DateOnly PlannedStart,
    DateOnly PlannedFinish,
    Guid? ApportionedToId,
    decimal? PercentCompleteOverride,
    Guid? ItemId,
    IReadOnlyList<EvmMilestoneDto> Milestones);

public sealed record EvmControlAccountDto(Guid WbsElementId, string Code, string Name, decimal Budget, int WorkPackageCount);

/// <summary>
/// The performance measurement baseline. Distributed budget (the work packages) + undistributed budget = PMB;
/// PMB + management reserve = contract budget base (CBB).
/// </summary>
public sealed record EvmBaselineDto(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    Guid BudgetVersionId,
    string BudgetVersionName,
    Guid? ScheduleBaselineId,
    string? ScheduleBaselineName,
    int ControlAccountLevel,
    int Revision,
    decimal DistributedBudget,
    decimal UndistributedBudget,
    decimal PerformanceMeasurementBaseline,
    decimal ManagementReserve,
    decimal ContractBudgetBase,
    decimal ThresholdPercent,
    decimal ThresholdAmount,
    bool HasClosedPeriods,
    DateTime CreatedAtUtc,
    string CreatedBy,
    IReadOnlyList<EvmControlAccountDto> ControlAccounts,
    IReadOnlyList<EvmWorkPackageDto> WorkPackages,
    IReadOnlyList<string> Notes);

/// <summary>
/// Builds the PMB. <see cref="BudgetVersionId"/> defaults to the project's locked Baseline budget; <see cref="ScheduleBaselineId"/>
/// to the plan sheet's newest schedule baseline. Work packages are the WBS leaves with budget; control accounts are their
/// ancestors at <see cref="ControlAccountLevel"/> (top-level elements are level 1).
/// </summary>
public sealed record CreateEvmBaselineRequest(
    Guid? BudgetVersionId = null,
    Guid? ScheduleBaselineId = null,
    int ControlAccountLevel = 2,
    decimal ManagementReserve = 0,
    decimal UndistributedBudget = 0,
    EvTechnique DefaultTechnique = EvTechnique.PercentComplete);

public sealed record SaveEvmMilestoneRequest(string Name, decimal Weight, Guid? ItemId = null, DateOnly? PlannedDate = null);

/// <summary>
/// Changes a work package's technique (and milestones). Allowed until the first status period closes; after that, use a
/// baseline change request. Milestones: weights must add up to 100; null keeps the existing ones (or, when there are none,
/// creates one per plan subtask).
/// </summary>
public sealed record UpdateWorkPackageRequest(EvTechnique Technique, Guid? ApportionedToId = null, IReadOnlyList<SaveEvmMilestoneRequest>? Milestones = null);

public sealed record SetEvmThresholdsRequest(decimal ThresholdPercent, decimal ThresholdAmount);

/// <summary>A % complete (0–100) for the current status; null goes back to the plan sheet.</summary>
public sealed record SetWorkPackageProgressRequest(decimal? PercentComplete);

/// <summary>Null marks the milestone not complete.</summary>
public sealed record SetMilestoneCompletionRequest(DateOnly? CompletedOn);

public sealed record ReopenEvmPeriodRequest(string Reason);

/// <summary>Amounts for one status period (the change in the cumulative figures since the previous closed period).</summary>
public sealed record EvmPeriodAmounts(decimal Bcws, decimal Bcwp, decimal Acwp)
{
    public decimal Sv => Bcwp - Bcws;
    public decimal Cv => Bcwp - Acwp;
}

/// <summary>
/// Cumulative earned value figures and what follows from them. Indices are null when their denominator is zero.
/// EAC (CPI) = ACWP + (BAC − BCWP) / CPI; EAC (CPI×SPI) = ACWP + (BAC − BCWP) / (CPI × SPI); EAC (manager) is the control
/// account manager's estimate, defaulting to EAC (CPI). <see cref="Eac"/>, <see cref="Etc"/> and <see cref="Vac"/> use the
/// manager's EAC. TCPI = (BAC − BCWP) / (BAC − ACWP); TCPI (EAC) = (BAC − BCWP) / (EAC − ACWP).
/// </summary>
public sealed record EvmMetrics(
    decimal Bac,
    decimal Bcws,
    decimal Bcwp,
    decimal Acwp,
    decimal Sv,
    decimal Cv,
    decimal? SvPercent,
    decimal? CvPercent,
    decimal? Spi,
    decimal? Cpi,
    decimal EacCpi,
    decimal EacCpiSpi,
    decimal EacManager,
    decimal Eac,
    decimal Etc,
    decimal Vac,
    decimal? Tcpi,
    decimal? TcpiEac,
    decimal PercentComplete,
    decimal PercentSpent);

public sealed record EvmWorkPackageStatusDto(
    Guid? WorkPackageId,
    Guid WbsElementId,
    string Code,
    string Name,
    EvTechnique Technique,
    decimal PercentComplete,
    string ProgressSource,
    EvmPeriodAmounts Period,
    EvmMetrics Cumulative);

public sealed record EvmControlAccountStatusDto(
    Guid WbsElementId,
    string Code,
    string Name,
    EvmPeriodAmounts Period,
    EvmMetrics Cumulative,
    bool NeedsVar,
    IReadOnlyList<string> VarianceReasons,
    bool VarComplete,
    IReadOnlyList<EvmWorkPackageStatusDto> WorkPackages);

public sealed record EvmPeriodRefDto(
    Guid FiscalPeriodId,
    string Name,
    DateOnly Start,
    DateOnly End,
    EvmPeriodState State,
    DateTime? ClosedAtUtc,
    string? ClosedBy);

/// <summary>
/// A project's status for one fiscal period. <see cref="Total"/> covers the distributed budget (the control accounts);
/// undistributed budget and management reserve are reported beside it, as on IPMDAR Format 1.
/// </summary>
public sealed record EvmStatusDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string? ContractNumber,
    EvmPeriodRefDto Period,
    bool IsFrozen,
    int BaselineRevision,
    IReadOnlyList<EvmControlAccountStatusDto> ControlAccounts,
    EvmPeriodAmounts PeriodTotal,
    EvmMetrics Total,
    decimal UndistributedBudget,
    decimal ManagementReserve,
    decimal PerformanceMeasurementBaseline,
    decimal ContractBudgetBase,
    decimal UnassignedActualCost,
    string CostBasis,
    IReadOnlyList<string> Notes);

/// <summary>One fiscal period of the S-curve. BCWP is known only for closed periods and the reporting period.</summary>
public sealed record EvmSeriesPointDto(
    Guid FiscalPeriodId,
    string Name,
    DateOnly Start,
    DateOnly End,
    EvmPeriodState State,
    decimal Bcws,
    decimal? Bcwp,
    decimal? Acwp,
    decimal? Cpi,
    decimal? Spi);

public sealed record EvmSeriesDto(Guid ProjectId, decimal Bac, IReadOnlyList<EvmSeriesPointDto> Points);

public sealed record EvmVarianceDto(
    Guid ControlAccountWbsId,
    string Code,
    string Name,
    Guid FiscalPeriodId,
    string PeriodName,
    bool NeedsVar,
    IReadOnlyList<string> Reasons,
    decimal Sv,
    decimal Cv,
    decimal? SvPercent,
    decimal? CvPercent,
    decimal Bac,
    decimal Eac,
    decimal Vac,
    string? Cause,
    string? Impact,
    string? CorrectiveAction,
    decimal? ManagerEac,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy,
    bool IsFrozen)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(Cause) && !string.IsNullOrWhiteSpace(Impact) && !string.IsNullOrWhiteSpace(CorrectiveAction);
}

public sealed record SaveVarianceRequest(string? Cause, string? Impact, string? CorrectiveAction, decimal? ManagerEac);

/// <summary>Earned value: the PMB, monthly status, period close and variance analysis. Finance, Admin or the project's manager.</summary>
public interface IEvmService
{
    /// <summary>The project's PMB, or null when it has none yet.</summary>
    Task<EvmBaselineDto?> GetBaselineAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Builds the PMB (or rebuilds it while no period is closed and no change request exists).</summary>
    Task<EvmBaselineDto> CreateBaselineAsync(Guid projectId, CreateEvmBaselineRequest request, CancellationToken cancellationToken = default);

    Task<EvmBaselineDto> UpdateWorkPackageAsync(Guid workPackageId, UpdateWorkPackageRequest request, CancellationToken cancellationToken = default);

    Task<EvmBaselineDto> SetThresholdsAsync(Guid projectId, SetEvmThresholdsRequest request, CancellationToken cancellationToken = default);

    Task<EvmBaselineDto> SetProgressAsync(Guid workPackageId, SetWorkPackageProgressRequest request, CancellationToken cancellationToken = default);

    Task<EvmBaselineDto> SetMilestoneCompletionAsync(Guid milestoneId, SetMilestoneCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The fiscal periods the PMB spans (and any closed outside it), oldest first.</summary>
    Task<IReadOnlyList<EvmPeriodRefDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The status for a fiscal period (default: the reporting period). Closed periods come from their frozen snapshot.</summary>
    Task<EvmStatusDto> GetStatusAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    Task<EvmSeriesDto> GetSeriesAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Freezes the period's status. Periods close in order; a period that hasn't started can't close.</summary>
    Task<EvmPeriodRefDto> ClosePeriodAsync(Guid projectId, Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>Reopens the latest closed period (Finance or Admin). The frozen snapshot is kept in the history.</summary>
    Task<EvmPeriodRefDto> ReopenPeriodAsync(Guid projectId, Guid fiscalPeriodId, ReopenEvmPeriodRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every control account's variances for the period (default: the reporting period) and its VAR narrative.</summary>
    Task<IReadOnlyList<EvmVarianceDto>> ListVariancesAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    /// <summary>Saves a control account's VAR for an open period.</summary>
    Task<EvmVarianceDto> SaveVarianceAsync(Guid projectId, Guid fiscalPeriodId, Guid controlAccountWbsId, SaveVarianceRequest request, CancellationToken cancellationToken = default);
}

public sealed record EvmWorkPackageSnapshotDto(Guid WbsElementId, string WbsCode, EvTechnique Technique, decimal Budget, DateOnly PlannedStart, DateOnly PlannedFinish);

public sealed record EvmBaselineSnapshotDto(int Revision, decimal ManagementReserve, decimal UndistributedBudget, IReadOnlyList<EvmWorkPackageSnapshotDto> WorkPackages);

public sealed record EvmChangeLineDto(
    Guid? WbsElementId,
    string? WbsCode,
    EvmBudgetSourceKind Source,
    decimal BudgetChange,
    EvTechnique? NewTechnique,
    DateOnly? NewStart,
    DateOnly? NewFinish);

public sealed record EvmChangeRequestDto(
    Guid Id,
    Guid ProjectId,
    int Number,
    string Title,
    string Justification,
    EvmChangeStatus Status,
    string RequestedBy,
    DateTime RequestedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionNote,
    int? AppliedRevision,
    IReadOnlyList<EvmChangeLineDto> Lines,
    EvmBaselineSnapshotDto? Before,
    EvmBaselineSnapshotDto? After)
{
    public string DisplayNumber => $"BCR-{Number:000}";
}

/// <summary>
/// One change: <see cref="BudgetChange"/> from <see cref="Source"/> to the work package of <see cref="WbsElementId"/> (a WBS
/// leaf; a new work package is created when it has none), or into undistributed budget when <see cref="WbsElementId"/> is null.
/// A negative change returns budget to the source. Dates and technique re-plan the work package.
/// </summary>
public sealed record SaveChangeLineRequest(
    Guid? WbsElementId,
    EvmBudgetSourceKind Source,
    decimal BudgetChange,
    EvTechnique? NewTechnique = null,
    DateOnly? NewStart = null,
    DateOnly? NewFinish = null);

public sealed record CreateChangeRequestRequest(string Title, string Justification, IReadOnlyList<SaveChangeLineRequest> Lines);

public sealed record DecideChangeRequestRequest(string? Note);

/// <summary>
/// Baseline change requests: the PM (or Finance) logs them; Finance or Admin approves or rejects. Approval applies the change
/// to the PMB, raises its revision and keeps a before/after snapshot; closed periods keep their frozen figures.
/// </summary>
public interface IEvmChangeRequestService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<EvmChangeRequestDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<EvmChangeRequestDto> GetAsync(Guid changeRequestId, CancellationToken cancellationToken = default);

    Task<EvmChangeRequestDto> CreateAsync(Guid projectId, CreateChangeRequestRequest request, CancellationToken cancellationToken = default);

    Task<EvmChangeRequestDto> ApproveAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default);

    Task<EvmChangeRequestDto> RejectAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default);
}

public sealed record EvmExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>IPMDAR-style workbooks (.xlsx): Format 1 (cost by WBS) and Format 5 (variance narrative).</summary>
public interface IEvmExportService
{
    Task<EvmExportFile> ExportFormat1Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    Task<EvmExportFile> ExportFormat5Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);
}

/// <summary>Data for the dashboards' earned value widget.</summary>
public sealed record EvmWidgetData(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string PeriodName,
    EvmMetrics Total,
    IReadOnlyList<EvmSeriesPointDto> Points,
    int ControlAccountsNeedingVar);
