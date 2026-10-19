
namespace Workbench.Contracts.Hr;

public enum ChecklistKind { Onboarding, Offboarding }

public enum ChecklistOwner { Hr, It, Manager, Employee, Finance }

public enum ChecklistAction { None, DeprovisionAccess, RecoverAsset, FinalTimesheet, LeavePayout, EndBenefits }

public enum ChecklistStatus { Open, Completed, Cancelled }

public enum ChecklistTaskStatus { Open, Done, Skipped }

public static class ChecklistLabels
{
    public static string Label(ChecklistOwner o) => o switch
    {
        ChecklistOwner.Hr => "HR",
        ChecklistOwner.It => "IT",
        _ => o.ToString(),
    };
}

public sealed record ChecklistTemplateTaskDto(Guid Id, int Sequence, string Title, ChecklistOwner Owner, int DueOffsetDays);

public sealed record ChecklistTemplateDto(Guid Id, string Name, ChecklistKind Kind, bool IsActive, IReadOnlyList<ChecklistTemplateTaskDto> Tasks);

public sealed record SaveChecklistTemplateTask(string Title, ChecklistOwner Owner, int DueOffsetDays);

public sealed record SaveChecklistTemplateRequest(string Name, ChecklistKind Kind, bool IsActive, IReadOnlyList<SaveChecklistTemplateTask> Tasks);

public sealed record ChecklistTaskDto(
    Guid Id,
    Guid ChecklistId,
    int Sequence,
    string Title,
    ChecklistOwner Owner,
    string? AssigneeEmail,
    DateOnly DueDate,
    ChecklistAction Action,
    Guid? AssetId,
    ChecklistTaskStatus Status,
    string? CompletedBy,
    DateTime? CompletedAtUtc,
    string? Note,

    /// <summary>The signed-in user may complete it.</summary>
    bool CanComplete);

public sealed record EmployeeChecklistDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    ChecklistKind Kind,
    DateOnly AnchorDate,
    ChecklistStatus Status,
    string? TemplateName,
    TerminationReason? TerminationReason,
    decimal? LeavePayoutHours,
    bool FinalTimesheetApproved,
    DateTime? AccessDeprovisionedAtUtc,
    IReadOnlyList<ChecklistTaskDto> Tasks)
{
    public int OpenTasks => Tasks.Count(t => t.Status == ChecklistTaskStatus.Open);
}

public sealed record StartOnboardingRequest(Guid EmployeeId, Guid? TemplateId);

/// <summary>Sets the termination date, then lists the offboarding work: access, assets, final timesheet, leave payout, benefits.</summary>
public sealed record StartOffboardingRequest(Guid EmployeeId, DateOnly LastDay, TerminationReason Reason, Guid? TemplateId, string? Note);

public sealed record CompleteChecklistTaskRequest(string? Note);

/// <summary>A task waiting on the signed-in user (for the home page and "My tasks").</summary>
public sealed record MyChecklistTaskDto(ChecklistTaskDto Task, string EmployeeName, ChecklistKind Kind);

/// <summary>
/// Onboarding and offboarding. HR (and administrators) manage templates and start checklists. Each task belongs to HR, IT
/// (administrators), Finance, the manager or the employee, who complete it; tasks with an action run it (IT deactivates the
/// sign-in, Finance recovers an asset, HR records the leave payout and ends benefits).
/// </summary>
public interface IOnboardingService
{
    Task<IReadOnlyList<ChecklistTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    Task<ChecklistTemplateDto> SaveTemplateAsync(Guid? templateId, SaveChecklistTemplateRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeChecklistDto> StartOnboardingAsync(StartOnboardingRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeChecklistDto> StartOffboardingAsync(StartOffboardingRequest request, CancellationToken cancellationToken = default);

    /// <summary>HR: every checklist; supervisors: their reports'; employees: their own.</summary>
    Task<IReadOnlyList<EmployeeChecklistDto>> ListAsync(bool openOnly = true, CancellationToken cancellationToken = default);

    Task<EmployeeChecklistDto> GetAsync(Guid checklistId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MyChecklistTaskDto>> ListMyTasksAsync(CancellationToken cancellationToken = default);

    Task<EmployeeChecklistDto> CompleteTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default);

    /// <summary>HR: marks a task not needed.</summary>
    Task<EmployeeChecklistDto> SkipTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default);
}

// ---- Reports ----

public sealed record HeadcountRowDto(string Group, int Headcount, int FullTime, int PartTime, int Other);

public sealed record HeadcountReportDto(DateOnly AsOf, int Total, IReadOnlyList<HeadcountRowDto> ByOrgUnit, IReadOnlyList<HeadcountRowDto> ByEmploymentType);

public sealed record SeparationDto(Guid EmployeeId, string EmployeeName, DateOnly LastDay, TerminationReason? Reason, string? OrgUnitName);

public sealed record TurnoverReportDto(
    DateOnly From,
    DateOnly To,
    int StartHeadcount,
    int EndHeadcount,
    int Hires,
    int Separations,
    int Voluntary,
    int Involuntary,
    decimal AverageHeadcount,
    decimal TurnoverRate,
    IReadOnlyList<SeparationDto> SeparationList);

public sealed record Eeo1CellDto(EeoJobCategory JobCategory, EeoRace Race, EeoGender Gender, int Count);

/// <summary>EEO-1 Component 1 counts for one snapshot date. Restricted: HR and administrators.</summary>
public sealed record Eeo1ReportDto(DateOnly SnapshotDate, int Employees, int NotDisclosed, int MissingJobCategory, IReadOnlyList<Eeo1CellDto> Cells);

public sealed record Vets4212RowDto(EeoJobCategory JobCategory, int ProtectedVeteranEmployees, int TotalEmployees, int ProtectedVeteranHires, int TotalHires);

/// <summary>VETS-4212: protected veterans by job category at the period end, and hires in the 12-month period. Restricted.</summary>
public sealed record Vets4212ReportDto(DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<Vets4212RowDto> Rows, int MaxEmployees, int MinEmployees);

public sealed record BenefitSummaryRowDto(
    string PlanCode,
    string PlanName,
    BenefitPlanType Type,
    string? Coverage,
    int Enrolled,
    decimal EmployeeMonthly,
    decimal EmployerMonthly,
    string? ExpenseAccount);

public sealed record BenefitSummaryDto(DateOnly AsOf, int EligibleEmployees, int EnrolledEmployees, int Waived, IReadOnlyList<BenefitSummaryRowDto> Rows)
{
    public decimal EmployerMonthlyTotal => Rows.Sum(r => r.EmployerMonthly);

    public decimal EmployeeMonthlyTotal => Rows.Sum(r => r.EmployeeMonthly);
}

public sealed record HrExportFile(string FileName, string ContentType, string Content);

/// <summary>
/// HR reports. Headcount and turnover, the benefits summary: HR, Finance and administrators. Expirations: HR and
/// administrators (supervisors see their reports' through <see cref="IHrPersonnelService.ListExpiringCredentialsAsync"/>).
/// EEO-1 and VETS-4212: HR and administrators only; exports are security events.
/// </summary>
public interface IHrReportService
{
    Task<HeadcountReportDto> GetHeadcountAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    Task<TurnoverReportDto> GetTurnoverAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<Eeo1ReportDto> GetEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default);

    Task<HrExportFile> ExportEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default);

    Task<Vets4212ReportDto> GetVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default);

    Task<HrExportFile> ExportVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default);

    Task<BenefitSummaryDto> GetBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    Task<HrExportFile> ExportBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default);
}

/// <summary>What the signed-in user may do in HR, for shaping the menu and pages (cosmetic; the server checks again).</summary>
public sealed record HrAccessDto(bool IsHr, bool IsFinance, bool IsSupervisor, Guid? EmployeeId, bool HasOpenTasks);

public interface IHrAccessService
{
    Task<HrAccessDto> GetAsync(CancellationToken cancellationToken = default);
}
