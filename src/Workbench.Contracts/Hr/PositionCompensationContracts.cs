using Workbench.Contracts.Payroll;

namespace Workbench.Contracts.Hr;

public enum PositionStatus { Proposed, Approved, Closed }

/// <summary>An approved position. Salary ranges are shown to HR, Finance and administrators only (null otherwise).</summary>
public sealed record PositionDto(
    Guid Id,
    string Code,
    string Title,
    Guid? LaborCategoryId,
    string? LaborCategoryName,
    Guid? OrgUnitId,
    string? OrgUnitName,
    Guid? ReportsToPositionId,
    string? ReportsToCode,
    EeoJobCategory? EeoJobCategory,
    bool IsExempt,
    EmploymentTypeKind EmploymentType,
    decimal? SalaryMin,
    decimal? SalaryMax,
    int Headcount,
    int Filled,
    PositionStatus Status,
    IReadOnlyList<string> Incumbents)
{
    public int Open => Status == PositionStatus.Closed ? 0 : Math.Max(0, Headcount - Filled);
}

public sealed record SavePositionRequest(
    string Code,
    string Title,
    Guid? LaborCategoryId,
    Guid? OrgUnitId,
    Guid? ReportsToPositionId,
    EeoJobCategory? EeoJobCategory,
    bool IsExempt,
    EmploymentTypeKind EmploymentType,
    decimal? SalaryMin,
    decimal? SalaryMax,
    int Headcount,
    PositionStatus Status);

/// <summary>Everyone reads the position list (without salary ranges unless HR/Finance/Admin); HR and administrators change it.</summary>
public interface IPositionService
{
    Task<IReadOnlyList<PositionDto>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default);

    Task<PositionDto> GetAsync(Guid positionId, CancellationToken cancellationToken = default);

    Task<PositionDto> CreateAsync(SavePositionRequest request, CancellationToken cancellationToken = default);

    Task<PositionDto> UpdateAsync(Guid positionId, SavePositionRequest request, CancellationToken cancellationToken = default);
}

public enum CompensationReason { NewHire, Merit, Promotion, MarketAdjustment, Equity, Correction, Other }

public enum CompensationChangeStatus { Draft, Pending, Applied, Rejected, Cancelled }

public enum MeritCycleStatus { Planning, Submitted, Closed }

public sealed record CompensationChangeDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid? CycleId,
    DateOnly EffectiveFrom,
    PayTypeKind PayType,
    decimal? AnnualSalary,
    decimal? HourlyRate,
    decimal? Annualized,
    decimal? PreviousAnnualized,
    CompensationReason Reason,
    string? Note,
    CompensationChangeStatus Status,
    string RequestedBy,
    DateTime CreatedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,

    /// <summary>"Waiting for HR" / "Waiting for Finance" while pending.</summary>
    string? WaitingOn)
{
    public decimal? Increase => Annualized - PreviousAnnualized;

    public decimal? IncreasePercent => PreviousAnnualized is > 0 && Increase is { } inc ? Math.Round(inc / PreviousAnnualized.Value, 4) : null;
}

/// <summary>An employee's compensation: the pay rate history payroll reads (newest first) and changes in flight.</summary>
public sealed record CompensationHistoryDto(
    Guid EmployeeId,
    string EmployeeName,
    PositionDto? Position,
    IReadOnlyList<PayRateDto> Rates,
    IReadOnlyList<CompensationChangeDto> Changes);

/// <summary>
/// A salary or hourly rate change. Salaried employees need <see cref="AnnualSalary"/>, hourly ones <see cref="HourlyRate"/>.
/// The effective date can't fall in a pay period whose payroll is already posted.
/// </summary>
public sealed record ProposeCompensationRequest(
    DateOnly EffectiveFrom,
    PayTypeKind PayType,
    decimal? AnnualSalary,
    decimal? HourlyRate,
    CompensationReason Reason,
    string? Note);

public sealed record MeritCycleDto(
    Guid Id,
    string Name,
    DateOnly EffectiveFrom,
    decimal BudgetPercent,
    MeritCycleStatus Status,
    decimal CoveredPay,
    decimal Budget,
    decimal Proposed,
    IReadOnlyList<CompensationChangeDto> Proposals)
{
    public decimal Remaining => Budget - Proposed;
}

public sealed record SaveMeritCycleRequest(string Name, DateOnly EffectiveFrom, decimal BudgetPercent);

/// <summary>Adds (or replaces) draft proposals: each employee's pay in effect on the cycle date raised by <see cref="Percent"/>.</summary>
public sealed record AddMeritProposalsRequest(IReadOnlyList<Guid> EmployeeIds, decimal Percent, CompensationReason Reason = CompensationReason.Merit);

public sealed record CompensationDecisionRequest(string? Comment);

/// <summary>
/// Compensation history and changes: HR, Finance and administrators. HR proposes; the approval engine routes each change to
/// HR, then Finance when the annualized increase is over <c>Hr:CompensationFinanceThreshold</c>. The final approval writes
/// the pay rate history that payroll (and the Paylocity employee sync) reads.
/// </summary>
public interface ICompensationService
{
    Task<CompensationHistoryDto> GetHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Changes waiting for a decision (pending), newest first.</summary>
    Task<IReadOnlyList<CompensationChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default);

    Task<CompensationChangeDto> ProposeAsync(Guid employeeId, ProposeCompensationRequest request, CancellationToken cancellationToken = default);

    Task<CompensationChangeDto> ApproveAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default);

    Task<CompensationChangeDto> RejectAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default);

    Task<CompensationChangeDto> CancelAsync(Guid changeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MeritCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default);

    Task<MeritCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default);

    Task<MeritCycleDto> CreateCycleAsync(SaveMeritCycleRequest request, CancellationToken cancellationToken = default);

    Task<MeritCycleDto> AddProposalsAsync(Guid cycleId, AddMeritProposalsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submits every draft proposal of the cycle for approval.</summary>
    Task<MeritCycleDto> SubmitCycleAsync(Guid cycleId, CancellationToken cancellationToken = default);
}
