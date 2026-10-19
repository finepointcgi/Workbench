namespace Workbench.Contracts.Payroll;

// Payroll integration contracts. Workbench never calculates payroll taxes: it exports approved hours to the payroll
// provider, imports the provider's register, reconciles it with the labor it accrued and posts the payroll journal.
// Every service here is for Finance and administrators only. Pay rates are masked unless explicitly revealed.

// Enums mirror Workbench.Domain.Payroll by name and value.
public enum PayFrequencyKind { Weekly, Biweekly, SemiMonthly, Monthly }

public enum PayTypeKind { Hourly, Salaried }

public enum EmploymentTypeKind { FullTime, PartTime, Temporary, Seasonal, Intern }

public enum PayrollRunState { Draft, Exported, RegisterImported, Posted, Voided }

// ---- Pay groups and calendars ----

public sealed record PayGroupDto(
    Guid Id,
    string Code,
    string Name,
    PayFrequencyKind Frequency,
    string ProviderKey,
    string ProviderName,
    string CompanyCode,
    DateOnly AnchorDate,
    int CheckDateOffsetDays,
    bool IsActive,
    int EmployeeCount);

public sealed record SavePayGroupRequest(
    string Code,
    string Name,
    PayFrequencyKind Frequency,
    string ProviderKey,
    string CompanyCode,
    DateOnly AnchorDate,
    int CheckDateOffsetDays = 5,
    bool IsActive = true);

public sealed record PayPeriodDto(
    Guid Id,
    Guid PayGroupId,
    int Year,
    int Number,
    DateOnly Start,
    DateOnly End,
    DateOnly CheckDate,
    Guid? RunId,
    PayrollRunState? RunState);

public interface IPayGroupService
{
    Task<IReadOnlyList<PayGroupDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<PayGroupDto> GetAsync(Guid payGroupId, CancellationToken cancellationToken = default);

    Task<PayGroupDto> CreateAsync(SavePayGroupRequest request, CancellationToken cancellationToken = default);

    Task<PayGroupDto> UpdateAsync(Guid payGroupId, SavePayGroupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Generates the pay periods ending in <paramref name="year"/> (idempotent: existing periods are kept). Returns how many were added.</summary>
    Task<int> GeneratePeriodsAsync(Guid payGroupId, int year, CancellationToken cancellationToken = default);

    /// <summary>The group's pay periods, optionally for one year, with the run on each.</summary>
    Task<IReadOnlyList<PayPeriodDto>> ListPeriodsAsync(Guid payGroupId, int? year = null, CancellationToken cancellationToken = default);
}

// ---- Employee payroll profiles (sensitive) ----

/// <summary>
/// One row of an employee's pay rate history, in effect from <see cref="EffectiveFrom"/> until the next row.
/// <see cref="AnnualSalary"/> and <see cref="HourlyRate"/> are null unless revealed.
/// </summary>
public sealed record PayRateDto(
    DateOnly EffectiveFrom,
    PayTypeKind PayType,
    decimal? AnnualSalary,
    decimal? HourlyRate,
    string? Reason,
    string CreatedBy,
    DateTime CreatedAtUtc,
    bool IsCurrent);

/// <summary>
/// An employee's payroll profile. <see cref="AnnualSalary"/> and <see cref="HourlyRate"/> are null unless revealed:
/// lists never carry pay rates (<see cref="HasRate"/> says whether one is on file). Pay type, rate and
/// <see cref="EffectiveFrom"/> are those in effect today; <see cref="Rates"/> is the history (newest first, including
/// rates that start later), only on single-profile reads.
/// </summary>
public sealed record PayProfileDto(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string Email,
    bool IsExempt,
    bool HasProfile,
    Guid? PayGroupId,
    string? PayGroupCode,
    string? ProviderEmployeeId,
    PayTypeKind PayType,
    bool HasRate,
    decimal? AnnualSalary,
    decimal? HourlyRate,
    DateOnly? EffectiveFrom,
    DateTime? UpdatedAtUtc,
    EmploymentTypeKind EmploymentType = EmploymentTypeKind.FullTime,
    IReadOnlyList<PayRateDto>? Rates = null);

/// <summary>
/// Creates or changes a profile. Salaried employees need <see cref="AnnualSalary"/>, hourly ones <see cref="HourlyRate"/>.
/// Leave both rates null to keep the rate on file (so a masked form can change other fields). A changed rate is added
/// to the history from <see cref="EffectiveFrom"/> (default: the hire date for a new profile, otherwise today), with
/// <see cref="Reason"/>. <see cref="EmploymentType"/> null keeps the current one.
/// </summary>
public sealed record SavePayProfileRequest(
    Guid? PayGroupId,
    string? ProviderEmployeeId,
    PayTypeKind PayType,
    decimal? AnnualSalary = null,
    decimal? HourlyRate = null,
    DateOnly? EffectiveFrom = null,
    string? Reason = null,
    EmploymentTypeKind? EmploymentType = null);

public interface IPayrollProfileService
{
    /// <summary>Every active employee with their profile (masked).</summary>
    Task<IReadOnlyList<PayProfileDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<PayProfileDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Saves the profile (audited). Returns it masked.</summary>
    Task<PayProfileDto> SaveAsync(Guid employeeId, SavePayProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>The profile with its pay rate. Recorded in the security event log as <c>payroll.pay_rate_revealed</c>.</summary>
    Task<PayProfileDto> RevealAsync(Guid employeeId, CancellationToken cancellationToken = default);
}

// ---- Providers ----

/// <summary>A payroll provider adapter. Unsupported ones show as "coming soon".</summary>
public sealed record PayrollProviderDto(string Key, string Name, bool IsSupported, bool SupportsEmployeeSync, bool ApiConfigured, string Status);

/// <summary>
/// The earning codes for a provider: <c>REG</c>, <c>OT</c> and each leave charge code (<c>LV.PTO</c>) mapped to the
/// provider's earning code.
/// </summary>
public sealed record EarningCodeMapping(string Category, string EarningCode);

public sealed record PayrollProviderSettingsDto(
    string ProviderKey,
    IReadOnlyList<EarningCodeMapping> EarningCodes,
    bool ExportSalariedRegularHours,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

public sealed record SavePayrollProviderSettingsRequest(IReadOnlyList<EarningCodeMapping> EarningCodes, bool ExportSalariedRegularHours);

public enum EmployeeSyncAction { Create, Update, Terminate, Skip }

public sealed record EmployeeSyncItemDto(Guid EmployeeId, string EmployeeName, string? ProviderEmployeeId, EmployeeSyncAction Action, bool Succeeded, string Message);

public sealed record EmployeeSyncResultDto(string ProviderKey, bool DryRun, IReadOnlyList<EmployeeSyncItemDto> Items);

public interface IPayrollProviderService
{
    Task<IReadOnlyList<PayrollProviderDto>> ListProvidersAsync(CancellationToken cancellationToken = default);

    Task<PayrollProviderSettingsDto> GetSettingsAsync(string providerKey, CancellationToken cancellationToken = default);

    Task<PayrollProviderSettingsDto> SaveSettingsAsync(string providerKey, SavePayrollProviderSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the group's employees to the provider (create, update, terminate). <paramref name="dryRun"/> only lists what
    /// would change. Needs the provider's API to be configured.
    /// </summary>
    Task<EmployeeSyncResultDto> SyncEmployeesAsync(Guid payGroupId, bool dryRun, CancellationToken cancellationToken = default);
}

// ---- Payroll runs ----

public sealed record PayrollRunDto(
    Guid Id,
    Guid PayGroupId,
    string PayGroupCode,
    string ProviderKey,
    Guid PayPeriodId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly CheckDate,
    PayrollRunState Status,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime? ExportedAtUtc,
    string? ExportFileName,
    Guid? RegisterId,
    string? RegisterFileName,
    DateTime? RegisterImportedAtUtc,
    decimal? RegisterGross,
    Guid? JournalEntryId,
    string? JournalNumber,
    DateTime? PostedAtUtc,
    string? VoidReason);

/// <summary>What keeps an employee's hours from being complete for the period.</summary>
public enum HoursIssueKind { MissingTimesheet, UnapprovedTimesheet, NoPayProfile, NoProviderId, NoEarningCode }

public sealed record HoursIssueDto(HoursIssueKind Kind, string Message);

public sealed record LeaveHoursDto(string Category, string EarningCode, decimal Hours);

/// <summary>
/// One employee's approved hours for the pay period: regular, overtime (non-exempt employees, over 40 worked hours in a
/// workweek) and each leave code.
/// </summary>
public sealed record EmployeeHoursDto(
    Guid EmployeeId,
    string EmployeeName,
    string? ProviderEmployeeId,
    bool IsExempt,
    PayTypeKind PayType,
    decimal RegularHours,
    decimal OvertimeHours,
    IReadOnlyList<LeaveHoursDto> Leave,
    IReadOnlyList<HoursIssueDto> Issues)
{
    public decimal LeaveHours => Leave.Sum(l => l.Hours);

    public decimal TotalHours => RegularHours + OvertimeHours + LeaveHours;
}

public sealed record HoursPreviewDto(Guid RunId, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<EmployeeHoursDto> Employees)
{
    public int FlaggedCount => Employees.Count(e => e.Issues.Count > 0);
}

/// <summary>Exporting with flagged employees needs <see cref="AcknowledgeIssues"/>: their unapproved hours are left out.</summary>
public sealed record ExportHoursRequest(bool AcknowledgeIssues = false);

public sealed record PayrollFileDto(string FileName, string ContentType, string Content);

/// <summary>A register file's content (CSV text).</summary>
public sealed record ImportRegisterRequest(string FileName, string Content);

public sealed record RegisterLineDto(
    Guid? EmployeeId,
    string ProviderEmployeeId,
    string EmployeeName,
    DateOnly CheckDate,
    decimal GrossWages,
    decimal EmployeeTaxes,
    decimal EmployerTaxes,
    decimal Deductions,
    decimal EmployerBenefits,
    decimal NetPay);

public sealed record RegisterImportResultDto(Guid RunId, Guid RegisterId, bool AlreadyImported, IReadOnlyList<RegisterLineDto> Lines, IReadOnlyList<string> Warnings);

/// <summary>
/// One employee's reconciliation: the register's gross against labor at pay rates. <see cref="ExpectedGross"/> =
/// worked hours at the pay rate + leave pay + overtime premium + salaried variance; <see cref="Unexplained"/> is what
/// remains. <see cref="AccruedLaborCost"/> is what the GL accrued (approved hours at cost rates, credited to 2100);
/// the journal posts gross minus that to the labor variance account.
/// </summary>
public sealed record ReconciliationRowDto(
    Guid? EmployeeId,
    string EmployeeName,
    PayTypeKind? PayType,
    decimal WorkedHours,
    decimal OvertimeHours,
    decimal LeaveHours,
    decimal? PayRate,
    decimal WorkedAtPayRate,
    decimal LeavePay,
    decimal OvertimePremium,
    decimal SalariedVariance,
    decimal ExpectedGross,
    decimal RegisterGross,
    decimal Unexplained,
    decimal AccruedLaborCost,
    decimal LaborVariance,
    IReadOnlyList<string> Notes);

public sealed record PayrollReconciliationDto(
    Guid RunId,
    IReadOnlyList<ReconciliationRowDto> Rows,
    ReconciliationRowDto Totals,
    decimal EmployerTaxes,
    decimal EmployerBenefits,
    decimal NetPay,
    decimal Withholding);

public sealed record VoidPayrollRequest(string Reason);

public interface IPayrollRunService
{
    Task<IReadOnlyList<PayrollRunDto>> ListAsync(Guid? payGroupId = null, CancellationToken cancellationToken = default);

    Task<PayrollRunDto> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Step 1: the run for a pay group and period (created if needed; a voided run is replaced by a new one).</summary>
    Task<PayrollRunDto> StartAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default);

    /// <summary>Step 2: approved hours by employee, with employees whose time is missing or unapproved flagged.</summary>
    Task<HoursPreviewDto> PreviewHoursAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Step 3: the provider's hours import file. Re-exporting before the register is imported regenerates it.</summary>
    Task<PayrollFileDto> ExportHoursAsync(Guid runId, ExportHoursRequest request, CancellationToken cancellationToken = default);

    /// <summary>Step 4: imports the provider's payroll register. The same file again is a no-op.</summary>
    Task<RegisterImportResultDto> ImportRegisterAsync(Guid runId, ImportRegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>The register lines in use.</summary>
    Task<IReadOnlyList<RegisterLineDto>> GetRegisterAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Step 5: register gross against labor at pay rates.</summary>
    Task<PayrollReconciliationDto> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Step 6: posts the payroll journal (idempotent per register) and runs leave accrual for the period.</summary>
    Task<PayrollRunDto> PostAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Reverses the posted journal and the period's leave accrual, and voids the run (reason required).</summary>
    Task<PayrollRunDto> VoidAsync(Guid runId, VoidPayrollRequest request, CancellationToken cancellationToken = default);
}
