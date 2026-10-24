namespace Workbench.Domain.Payroll;

// Payroll integration. Workbench never calculates payroll taxes: it sends approved hours to the payroll provider
// (Paylocity first), imports the provider's payroll register, reconciles it with the labor it accrued, and posts the
// payroll journal. Pay rates here are what employees are paid, not the cost rates projects are charged.

public enum PayFrequency
{
    Weekly,
    Biweekly,
    SemiMonthly,
    Monthly,
}

public enum PayType
{
    Hourly,
    Salaried,
}

/// <summary>How an employee is employed. Leave policies can be limited to some of these.</summary>
public enum EmploymentType
{
    FullTime,
    PartTime,
    Temporary,
    Seasonal,
    Intern,
}

/// <summary>Employees paid together on one schedule by one provider company.</summary>
public sealed class PayGroup : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public PayFrequency Frequency { get; set; } = PayFrequency.Biweekly;

    /// <summary>The payroll provider adapter, e.g. <c>paylocity</c>.</summary>
    public required string ProviderKey { get; set; }

    /// <summary>The company code (Paylocity company id) the provider files this group under.</summary>
    public required string CompanyCode { get; set; }

    /// <summary>
    /// The first day of any one pay period: weekly and biweekly calendars step from it. Ignored for semi-monthly
    /// (1st–15th, 16th–end) and monthly calendars.
    /// </summary>
    public DateOnly AnchorDate { get; set; }

    /// <summary>Days after the period end that the checks are dated.</summary>
    public int CheckDateOffsetDays { get; set; } = 5;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One pay period of a pay group. Generated per calendar year from the group's frequency.</summary>
public sealed class PayPeriod : Entity
{
    public Guid PayGroupId { get; set; }

    /// <summary>The year of <see cref="End"/>; <see cref="Number"/> counts from 1 within it.</summary>
    public int Year { get; set; }

    public int Number { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public DateOnly CheckDate { get; set; }
}

/// <summary>
/// How an employee is paid. Sensitive: Finance and administrators only, and the rate is masked until revealed (which is
/// recorded as a security event). The rate history is <see cref="EmployeePayRate"/>; <see cref="PayType"/>,
/// <see cref="AnnualSalary"/>, <see cref="HourlyRate"/> and <see cref="EffectiveFrom"/> mirror its latest row. Anything
/// that needs the rate for a date (exports, reconciliation) reads the history (<c>Application.Payroll.PayRates</c>).
/// </summary>
public sealed class EmployeePayProfile : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid? PayGroupId { get; set; }

    /// <summary>The employee's id in the payroll provider (Paylocity employee id).</summary>
    public string? ProviderEmployeeId { get; set; }

    public PayType PayType { get; set; } = PayType.Hourly;

    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;

    /// <summary>Salaried employees: annual salary.</summary>
    public decimal? AnnualSalary { get; set; }

    /// <summary>Hourly employees: hourly pay rate.</summary>
    public decimal? HourlyRate { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Hourly pay equivalent: the hourly rate, or the salary over 2,080 hours.</summary>
    public decimal? EquivalentHourlyRate => PayType == PayType.Salaried
        ? AnnualSalary is { } salary ? Math.Round(salary / 2080m, 4, MidpointRounding.AwayFromZero) : null
        : HourlyRate;
}

/// <summary>
/// An employee's pay rate from <see cref="EffectiveFrom"/> until the next row starts. Append-only: a change or a
/// correction is a new row with a reason; for the same effective date the latest recorded row wins.
/// </summary>
public sealed class EmployeePayRate : Entity
{
    public Guid EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public PayType PayType { get; set; }
    public decimal? AnnualSalary { get; set; }
    public decimal? HourlyRate { get; set; }
    public string? Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Hourly pay equivalent: the hourly rate, or the salary over 2,080 hours.</summary>
    public decimal? EquivalentHourlyRate => PayType == PayType.Salaried
        ? AnnualSalary is { } salary ? Math.Round(salary / 2080m, 4, MidpointRounding.AwayFromZero) : null
        : HourlyRate;
}

/// <summary>Per-provider settings: the earning codes hours are exported under.</summary>
public sealed class PayrollProviderSetting : Entity
{
    public required string ProviderKey { get; set; }

    /// <summary>
    /// <c>key=code</c> pairs separated by <c>;</c>. Keys are <c>REG</c> (regular), <c>OT</c> (overtime) and leave charge
    /// codes (<c>LV.PTO</c>, <c>LV.HOL</c>...); values are the provider's earning codes.
    /// </summary>
    public string EarningCodes { get; set; } = "";

    /// <summary>When false (the default), salaried employees' regular hours aren't exported: the provider pays their salary.</summary>
    public bool ExportSalariedRegularHours { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public enum PayrollRunStatus
{
    Draft,
    Exported,
    RegisterImported,
    Posted,
    Voided,
}

/// <summary>Payroll for one pay group and pay period: export hours, import the register, reconcile, post.</summary>
public sealed class PayrollRun : Entity
{
    public Guid PayGroupId { get; set; }
    public Guid PayPeriodId { get; set; }
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ExportedAtUtc { get; set; }
    public string? ExportedBy { get; set; }
    public string? ExportFileName { get; set; }

    /// <summary>The file as exported, kept as evidence of what was sent to the provider.</summary>
    public string? ExportContent { get; set; }

    /// <summary>The register currently in use (the latest import that isn't voided).</summary>
    public Guid? RegisterId { get; set; }

    public DateTime? PostedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public Guid? JournalEntryId { get; set; }

    public DateTime? VoidedAtUtc { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }

    public List<PayrollExportLine> ExportLines { get; } = [];
}

/// <summary>One exported line: an employee's hours under one earning code.</summary>
public sealed class PayrollExportLine : Entity
{
    public Guid RunId { get; set; }
    public Guid EmployeeId { get; set; }

    /// <summary><c>REG</c>, <c>OT</c> or a leave charge code (<c>LV.PTO</c>).</summary>
    public required string Category { get; set; }

    public required string EarningCode { get; set; }
    public decimal Hours { get; set; }
}

public enum PayrollRegisterStatus
{
    Active,
    Replaced,
    Voided,
}

/// <summary>An imported payroll register. Posting is idempotent per register: its id is the journal entry's source id.</summary>
public sealed class PayrollRegister : Entity
{
    public Guid RunId { get; set; }
    public required string FileName { get; set; }

    /// <summary>SHA-256 of the file: importing the same file again returns this register.</summary>
    public required string ContentHash { get; set; }

    public PayrollRegisterStatus Status { get; set; } = PayrollRegisterStatus.Active;
    public required string ImportedBy { get; set; }
    public DateTime ImportedAtUtc { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }

    public List<PayrollRegisterLine> Lines { get; } = [];
}

/// <summary>One check in the register: what the provider paid an employee.</summary>
public sealed class PayrollRegisterLine : Entity
{
    public Guid RegisterId { get; set; }
    public Guid? EmployeeId { get; set; }
    public required string ProviderEmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public DateOnly CheckDate { get; set; }
    public decimal GrossWages { get; set; }
    public decimal EmployeeTaxes { get; set; }
    public decimal EmployerTaxes { get; set; }
    public decimal Deductions { get; set; }
    public decimal EmployerBenefits { get; set; }
    public decimal NetPay { get; set; }
}

/// <summary>
/// The customer's own API credentials for a payroll provider (ADP, Paychex, Gusto). <see cref="ProtectedCredentials"/>
/// is encrypted with Data Protection and never logged or returned; <see cref="Summary"/> is what the setup screen shows.
/// </summary>
public sealed class PayrollProviderConnection : Entity
{
    public required string ProviderKey { get; set; }

    /// <summary><c>production</c> or <c>sandbox</c> (the provider's test environment).</summary>
    public string Environment { get; set; } = "production";

    public required string ProtectedCredentials { get; set; }

    /// <summary>Non-secret description, e.g. "Client id 3f2a…".</summary>
    public string? Summary { get; set; }

    public required string ConnectedBy { get; set; }
    public DateTime ConnectedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }
    public bool? LastCheckSucceeded { get; set; }
    public string? LastCheckMessage { get; set; }
}
