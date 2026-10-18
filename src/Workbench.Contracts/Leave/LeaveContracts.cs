namespace Workbench.Contracts.Leave;

// Leave management: policies with accrual tiers, balances (an append-only ledger per employee and policy), requests
// approved by the supervisor, and the holiday calendar. Leave is taken by charging the policy's Leave charge code on
// an approved timesheet.

// Enums mirror Workbench.Domain.Leave by name and value.
public enum LeaveTypeKind { Pto, Sick, Holiday, Other }

public enum AccrualMethodKind { None, PerPayPeriod, PerHourWorked, AnnualGrant }

public enum LeaveLedgerEntryKind { Accrual, Grant, Usage, Adjustment, Forfeiture }

public enum LeaveRequestState { Pending, Approved, Rejected, Cancelled }

// ---- Policies ----

public sealed record AccrualTierDto(int MinYearsOfService, decimal Rate, decimal? MaxBalance = null);

public sealed record LeavePolicyDto(
    Guid Id,
    string Code,
    string Name,
    LeaveTypeKind Type,
    Guid WbsElementId,
    string WbsCode,
    AccrualMethodKind Method,
    IReadOnlyList<AccrualTierDto> Tiers,
    decimal? MaxBalance,
    decimal? CarryoverLimit,
    int WaitingPeriodDays,
    bool AllowNegativeBalance,
    bool IsActive);

public sealed record SaveLeavePolicyRequest(
    string Code,
    string Name,
    LeaveTypeKind Type,
    Guid WbsElementId,
    AccrualMethodKind Method,
    IReadOnlyList<AccrualTierDto> Tiers,
    decimal? MaxBalance = null,
    decimal? CarryoverLimit = null,
    int WaitingPeriodDays = 0,
    bool AllowNegativeBalance = false,
    bool IsActive = true);

/// <summary>Leave charge code a policy can map to.</summary>
public sealed record LeaveChargeCodeDto(Guid WbsElementId, string Code, string Name);

/// <summary>Everyone can read policies; administrators change them.</summary>
public interface ILeavePolicyService
{
    Task<IReadOnlyList<LeavePolicyDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaveChargeCodeDto>> ListLeaveChargeCodesAsync(CancellationToken cancellationToken = default);

    Task<LeavePolicyDto> CreateAsync(SaveLeavePolicyRequest request, CancellationToken cancellationToken = default);

    Task<LeavePolicyDto> UpdateAsync(Guid policyId, SaveLeavePolicyRequest request, CancellationToken cancellationToken = default);
}

// ---- Holidays ----

public sealed record HolidayDto(Guid Id, DateOnly Date, string Name, decimal? Hours);

public sealed record SaveHolidayRequest(DateOnly Date, string Name, decimal? Hours = null);

/// <summary>Everyone reads the calendar; administrators change it.</summary>
public interface IHolidayService
{
    Task<IReadOnlyList<HolidayDto>> ListAsync(int year, CancellationToken cancellationToken = default);

    Task<HolidayDto> AddAsync(SaveHolidayRequest request, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid holidayId, CancellationToken cancellationToken = default);

    /// <summary>Adds the US federal holidays for <paramref name="year"/> (observed dates); existing dates are kept. Returns how many were added.</summary>
    Task<int> ApplyFederalTemplateAsync(int year, CancellationToken cancellationToken = default);
}

// ---- Balances ----

/// <summary>
/// An employee's balance in one policy. <see cref="Balance"/> includes usage from approved timesheets;
/// <see cref="Planned"/> is approved requests not yet on an approved timesheet; <see cref="Available"/> = balance − planned.
/// </summary>
public sealed record LeaveBalanceDto(
    Guid EmployeeId,
    string EmployeeName,
    Guid PolicyId,
    string PolicyCode,
    string PolicyName,
    LeaveTypeKind Type,
    string WbsCode,
    decimal Balance,
    decimal AccruedThisYear,
    decimal UsedThisYear,
    decimal Planned,
    decimal Available,
    bool AllowNegativeBalance);

public sealed record LeaveLedgerEntryDto(
    Guid Id,
    DateOnly Date,
    LeaveLedgerEntryKind Kind,
    decimal Hours,
    decimal BalanceAfter,
    string? Reason,
    string? SourceKey,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record AdjustLeaveRequest(Guid EmployeeId, Guid PolicyId, decimal Hours, string Reason, DateOnly? Date = null);

public sealed record AccrualRunResultDto(Guid PayPeriodId, DateOnly PeriodStart, DateOnly PeriodEnd, int EntriesAdded, decimal HoursAccrued, int AlreadyAccrued, IReadOnlyList<string> Notes);

public sealed record YearEndResultDto(int Year, int EmployeesForfeited, decimal HoursForfeited);

public interface ILeaveBalanceService
{
    /// <summary>The signed-in employee's balances in every active policy that accrues or has a balance.</summary>
    Task<IReadOnlyList<LeaveBalanceDto>> GetMyBalancesAsync(CancellationToken cancellationToken = default);

    /// <summary>Balances of everyone (Finance/Admin) or of the user's direct reports (supervisors).</summary>
    Task<IReadOnlyList<LeaveBalanceDto>> ListBalancesAsync(CancellationToken cancellationToken = default);

    /// <summary>The ledger behind a balance: the employee, their supervisor, Finance and administrators.</summary>
    Task<IReadOnlyList<LeaveLedgerEntryDto>> GetHistoryAsync(Guid employeeId, Guid policyId, CancellationToken cancellationToken = default);

    /// <summary>A manual adjustment with a reason (Finance/Admin, audited).</summary>
    Task<LeaveLedgerEntryDto> AdjustAsync(AdjustLeaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Accrues every accruing policy for the pay period's employees (Finance/Admin). Idempotent per pay period.</summary>
    Task<AccrualRunResultDto> RunAccrualAsync(Guid payPeriodId, CancellationToken cancellationToken = default);

    /// <summary>Forfeits hours above each policy's carryover limit at the end of <paramref name="year"/> (Finance/Admin). Idempotent.</summary>
    Task<YearEndResultDto> RunYearEndAsync(int year, CancellationToken cancellationToken = default);
}

// ---- Requests ----

public sealed record LeaveDayDto(DateOnly Date, decimal Hours);

public sealed record LeaveRequestDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid PolicyId,
    string PolicyName,
    string WbsCode,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Hours,
    IReadOnlyList<LeaveDayDto> Days,
    LeaveRequestState Status,
    string? Comment,
    DateTime CreatedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Requests leave on each business day from <see cref="StartDate"/> to <see cref="EndDate"/> (holidays skipped).
/// <see cref="HoursPerDay"/> defaults to the employee's standard day.
/// </summary>
public sealed record CreateLeaveRequest(Guid PolicyId, DateOnly StartDate, DateOnly EndDate, decimal? HoursPerDay = null, string? Comment = null);

public sealed record DecideLeaveRequest(string? Comment = null);

public enum TeamCalendarEntryKind { Leave, PendingLeave, Holiday }

public sealed record TeamCalendarEntryDto(DateOnly Date, TeamCalendarEntryKind Kind, Guid? EmployeeId, string Title, decimal? Hours, Guid? RequestId);

public interface ILeaveRequestService
{
    Task<IReadOnlyList<LeaveRequestDto>> ListMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Submits a request; the supervisor (an administrator, without one) is notified.</summary>
    Task<LeaveRequestDto> CreateAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>The employee withdraws a pending or approved request.</summary>
    Task<LeaveRequestDto> CancelAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<bool> IsApproverAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LeaveRequestDto>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default);

    Task<LeaveRequestDto> ApproveAsync(Guid requestId, DecideLeaveRequest request, CancellationToken cancellationToken = default);

    Task<LeaveRequestDto> RejectAsync(Guid requestId, DecideLeaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approved (and pending) leave and holidays in [from, to] for the user, their supervisor, peers and direct reports;
    /// everyone for timekeepers, Finance and administrators.
    /// </summary>
    Task<IReadOnlyList<TeamCalendarEntryDto>> GetTeamCalendarAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

// ---- Timesheet integration ----

/// <summary>Hours to pre-fill on a timesheet: an approved leave request day or a holiday.</summary>
public sealed record LeavePrefillDto(Guid WbsElementId, string WbsCode, DateOnly Date, decimal Hours, string Source);

/// <summary>Leave hours on a timesheet against the balance available for them.</summary>
public sealed record TimesheetLeaveCheckDto(Guid PolicyId, string PolicyName, string WbsCode, decimal HoursOnTimesheet, decimal Available, bool AllowNegativeBalance)
{
    public bool Exceeds => HoursOnTimesheet > Available;

    public bool Blocks => Exceeds && !AllowNegativeBalance;
}

public sealed record TimesheetLeaveDto(Guid TimesheetId, IReadOnlyList<LeavePrefillDto> Prefill, IReadOnlyList<TimesheetLeaveCheckDto> Checks)
{
    public IEnumerable<string> Warnings => Checks.Where(c => c.Exceeds).Select(c => c.Blocks
        ? $"{c.HoursOnTimesheet:0.##} h of {c.PolicyName} is more than the {c.Available:0.##} h available; the policy doesn't allow a negative balance."
        : $"{c.HoursOnTimesheet:0.##} h of {c.PolicyName} is more than the {c.Available:0.##} h available.");
}

/// <summary>Leave information for the employee's own timesheet.</summary>
public interface ILeaveTimesheetService
{
    Task<TimesheetLeaveDto> GetForTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default);
}
