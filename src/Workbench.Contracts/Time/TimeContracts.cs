using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Contracts.Time;

// Enum mirrors Workbench.Domain.ProjectAccounting.TimesheetStatus by name and value.
public enum TimesheetState { Draft, Submitted, Approved, Rejected }

public static class TimesheetCertification
{
    /// <summary>The attestation an employee accepts when submitting. Stored with the timesheet.</summary>
    public const string Text =
        "I certify that this timesheet is a true and accurate record of all hours I worked during this period, "
        + "charged to the correct projects and charge codes, including indirect and leave hours. I understand that "
        + "falsifying time records is a violation of company policy and may violate federal law (18 U.S.C. 1001).";
}

public sealed record TimesheetDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    string EmployeeEmail,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    TimesheetState Status,
    int Revision,
    decimal TotalHours,
    decimal StandardHours,
    DateTime? CertifiedAtUtc,
    DateTime? SubmittedAtUtc,
    DateTime? FirstSubmittedAtUtc,
    string? ReviewedBy,
    DateTime? ApprovedAtUtc,
    DateTime? RejectedAtUtc,
    string? RejectionReason,
    DateTime UpdatedAtUtc)
{
    /// <summary>Submitted and approved timesheets can't be edited; an approved one must be reopened with a reason.</summary>
    public bool IsLocked => Status is TimesheetState.Submitted or TimesheetState.Approved;

    /// <summary>After the first submission every entry change needs a reason and writes a revision.</summary>
    public bool ChangesNeedReason => FirstSubmittedAtUtc is not null;
}

public sealed record TimeEntryDto(
    Guid Id,
    Guid WbsElementId,
    string WbsCode,
    string WbsName,
    string ProjectCode,
    Guid LaborCategoryId,
    string LaborCategoryCode,
    DateOnly WorkDate,
    decimal Hours,
    string? Comment,
    DateTime EnteredAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TimeEntryRevisionDto(
    Guid Id,
    Guid TimeEntryId,
    DateOnly WorkDate,
    int TimesheetRevision,
    decimal? OldHours,
    decimal? NewHours,
    string? OldWbsCode,
    string? NewWbsCode,
    string? OldLaborCategoryCode,
    string? NewLaborCategoryCode,
    string ChangedBy,
    DateTime ChangedAtUtc,
    string Reason);

/// <summary>A charge code usable somewhere in the timesheet period, and the dates it may be charged.</summary>
/// <summary>A code the employee is staffed or assigned on that they can't charge yet, and when or why it opens.</summary>
public sealed record UpcomingChargeCodeDto(Guid WbsElementId, string Code, string Name, string ProjectCode, string Reason);

public sealed record TimesheetChargeCodeDto(ChargeCodeDto ChargeCode, IReadOnlyList<DateOnly> ChargeableDates, bool RecentlyUsed);

public sealed record TimesheetDayDto(
    DateOnly Date,
    bool IsFuture,
    bool IsInOpenPeriod,
    bool IsEmployed,
    DateTime? FirstEnteredAtUtc,
    bool EnteredLate)
{
    public bool CanEnter => !IsFuture && IsInOpenPeriod && IsEmployed;
}

public sealed record TimesheetDetailDto(
    TimesheetDto Timesheet,
    IReadOnlyList<TimeEntryDto> Entries,
    IReadOnlyList<TimeEntryRevisionDto> Revisions,
    IReadOnlyList<TimesheetDayDto> Days,
    IReadOnlyList<TimesheetChargeCodeDto> ChargeCodes,
    IReadOnlyList<AuditEventDto> Audit,

    /// <summary>Codes they're on but can't charge yet (the project is still being planned, or their dates start later), with when it opens.</summary>
    IReadOnlyList<UpcomingChargeCodeDto>? NotOpenYet = null);

/// <summary>
/// One cell of the grid. <see cref="EntryId"/> identifies an existing entry (needed to move it to another charge
/// code); without it the entry is matched by charge code and date. Zero hours removes the entry.
/// <see cref="LaborCategoryId"/> defaults to the charge code's labor category.
/// </summary>
public sealed record TimeEntryInput(Guid WbsElementId, DateOnly WorkDate, decimal Hours, string? Comment = null, Guid? EntryId = null, Guid? LaborCategoryId = null);

/// <summary>
/// Replaces the entries of one day (when <see cref="Day"/> is set) or of the whole timesheet: entries in scope that
/// aren't listed are deleted. <see cref="Reason"/> is required once the timesheet has been submitted.
/// </summary>
public sealed record SaveTimeEntriesRequest(IReadOnlyList<TimeEntryInput> Entries, DateOnly? Day = null, string? Reason = null);

/// <summary>Submitting requires <see cref="Certify"/> = true: the employee accepts <see cref="TimesheetCertification.Text"/>.</summary>
public sealed record SubmitTimesheetRequest(bool Certify);

public sealed record SubmitTimesheetResult(TimesheetDetailDto Detail, IReadOnlyList<string> Warnings);

public sealed record ReasonRequest(string Reason);

/// <summary>The signed-in employee's own timesheets.</summary>
public interface ITimesheetService
{
    /// <summary>The current employee's timesheet for the period containing <paramref name="date"/>, created as a draft if needed.</summary>
    Task<TimesheetDetailDto> GetOrCreateMyTimesheetAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>The current employee's timesheets, newest first.</summary>
    Task<IReadOnlyList<TimesheetDto>> ListMyTimesheetsAsync(CancellationToken cancellationToken = default);

    /// <summary>A timesheet with entries, revisions and audit trail. Visible to the employee and their approver.</summary>
    Task<TimesheetDetailDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default);

    Task<TimesheetDetailDto> SaveEntriesAsync(Guid timesheetId, SaveTimeEntriesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submits with the employee's certification. Warnings (e.g. fewer hours than standard) don't block submitting.</summary>
    Task<SubmitTimesheetResult> SubmitAsync(Guid timesheetId, SubmitTimesheetRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reopens a submitted (recall) or approved (correction) timesheet as a draft. Needs a reason; reopening an approved
    /// timesheet increments its revision. Either way it must be certified and approved again.
    /// </summary>
    Task<TimesheetDetailDto> ReopenAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default);
}

public sealed record TeamMemberStatusDto(
    Guid EmployeeId,
    string EmployeeName,
    Guid? TimesheetId,
    TimesheetState? Status,
    decimal Hours,
    decimal StandardHours,
    bool IsMissing);

public sealed record TeamStatusDto(DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<TeamMemberStatusDto> Members);

/// <summary>Approving the timesheets of the current user's direct reports (or, for admins, of employees without a supervisor).</summary>
public interface ITimesheetApprovalService
{
    /// <summary>True when the current user approves anyone's timesheets.</summary>
    Task<bool> IsApproverAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TimesheetDto>> ListPendingAsync(CancellationToken cancellationToken = default);

    Task<TimesheetDto> ApproveAsync(Guid timesheetId, CancellationToken cancellationToken = default);

    Task<TimesheetDto> RejectAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Each direct report's timesheet state and hours for the period containing <paramref name="date"/>.</summary>
    Task<TeamStatusDto> GetTeamStatusAsync(DateOnly date, CancellationToken cancellationToken = default);
}

public sealed record HoursByChargeCodeRow(Guid EmployeeId, string EmployeeName, Guid WbsElementId, string WbsCode, string ProjectCode, decimal Hours);

public sealed record DailyEntryComplianceRow(
    Guid EmployeeId,
    string EmployeeName,
    DateOnly WorkDate,
    decimal Hours,
    DateTime FirstEnteredAtUtc,
    int BusinessDaysAfterWorkDate,
    bool IsLate);

public interface ITimeReportService
{
    /// <summary>Hours by employee and charge code for work dates in [from, to]; approved timesheets only unless <paramref name="approvedOnly"/> is false.</summary>
    Task<IReadOnlyList<HoursByChargeCodeRow>> GetHoursByChargeCodeAsync(DateOnly from, DateOnly to, bool approvedOnly = true, CancellationToken cancellationToken = default);

    /// <summary>When each work day's hours were first entered, flagging days entered later than the configured business-day limit.</summary>
    Task<IReadOnlyList<DailyEntryComplianceRow>> GetDailyEntryComplianceAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approved labor as CSV: employee, date, project, WBS, labor category, hours and cost at the cost rate effective on
    /// the work date.
    /// </summary>
    Task<string> ExportLaborDistributionCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
