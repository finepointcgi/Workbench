namespace Workbench.Contracts.Payroll;

// Configurable payroll file layouts (Payroll › Providers): the hours export and the payroll register columns, so a
// customer's own Paylocity import and register formats are matched without code changes. Finance and administrators.

/// <summary>What an hours export column contains.</summary>
public enum HoursColumnValue
{
    CompanyCode,
    EmployeeId,
    EmployeeName,
    FirstName,
    LastName,
    PeriodBegin,
    PeriodEnd,
    CheckDate,

    /// <summary>The earning/deduction type; always "E" (earning) unless a constant is given.</summary>
    Det,

    /// <summary>The provider earning code (REG, OT, PTO...) from the earning code map.</summary>
    EarningCode,
    Hours,

    /// <summary>Left blank so the provider pays at the rate on file.</summary>
    Rate,

    /// <summary>Left blank: Workbench sends hours, never pay.</summary>
    Amount,

    /// <summary>The employee's org unit code.</summary>
    CostCenter,
    PayGroup,

    /// <summary>The fixed text in <see cref="HoursColumnDto.Constant"/>.</summary>
    Constant,
    Blank,
}

public sealed record HoursColumnDto(string Header, HoursColumnValue Value, string? Constant = null);

/// <summary>
/// The hours export: columns in order, the date format (.NET format, e.g. <c>MM/dd/yyyy</c>), the delimiter (<c>,</c>,
/// <c>|</c> or <c>\t</c>), whether a header row is written and how hours are formatted.
/// </summary>
public sealed record HoursLayoutDto(
    IReadOnlyList<HoursColumnDto> Columns,
    string DateFormat = "MM/dd/yyyy",
    string Delimiter = ",",
    bool IncludeHeader = true,
    string HoursFormat = "0.00");

/// <summary>A value Workbench reads from the payroll register.</summary>
public enum RegisterField { EmployeeId, EmployeeName, CheckDate, GrossPay, EmployeeTaxes, EmployerTaxes, Deductions, EmployerBenefits, NetPay }

/// <summary>A register field and the header spellings accepted for it (matched ignoring case, spaces and punctuation).</summary>
public sealed record RegisterColumnDto(RegisterField Field, IReadOnlyList<string> Headers);

/// <summary>Where an earning, tax or deduction code's amount goes when the register lists one row per code.</summary>
public enum RegisterAmountTarget { GrossPay, EmployeeTaxes, EmployerTaxes, Deductions, EmployerBenefits, NetPay, Ignore }

public sealed record RegisterCodeMapDto(string Code, RegisterAmountTarget Target);

/// <summary>
/// The payroll register: header spellings per field, accepted date formats and the delimiter. For a detail register
/// (one row per check and code), name the <see cref="CodeColumn"/> and <see cref="AmountColumn"/> and map each code to
/// the total it adds to; without a net pay code, net = gross − employee taxes − deductions.
/// </summary>
public sealed record RegisterLayoutDto(
    IReadOnlyList<RegisterColumnDto> Columns,
    IReadOnlyList<string> DateFormats,
    string Delimiter = ",",
    string? CodeColumn = null,
    string? AmountColumn = null,
    IReadOnlyList<RegisterCodeMapDto>? CodeMap = null);

public sealed record PayrollLayoutDto(
    string ProviderKey,
    HoursLayoutDto Hours,
    RegisterLayoutDto Register,
    bool HoursIsDefault,
    bool RegisterIsDefault,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record SavePayrollLayoutRequest(HoursLayoutDto Hours, RegisterLayoutDto Register);

/// <summary>Tests a register file against a layout (the saved one when <see cref="Layout"/> is null).</summary>
public sealed record TestRegisterSampleRequest(string Content, RegisterLayoutDto? Layout = null);

public sealed record RegisterColumnMatchDto(RegisterField Field, bool Required, string? MatchedHeader, int? Index);

public sealed record RegisterTestRowDto(
    int Line,
    bool Parsed,
    string? EmployeeId,
    string? EmployeeName,
    DateOnly? CheckDate,
    decimal? GrossPay,
    decimal? EmployeeTaxes,
    decimal? EmployerTaxes,
    decimal? Deductions,
    decimal? EmployerBenefits,
    decimal? NetPay,
    string? Message);

/// <summary>
/// What a sample register parses into: which fields were found under which header, each line's values or why it
/// couldn't be read, file headers no field uses, and codes the code map doesn't cover.
/// </summary>
public sealed record RegisterTestResultDto(
    bool Ok,
    string? Error,
    IReadOnlyList<string> Headers,
    IReadOnlyList<RegisterColumnMatchDto> Columns,
    IReadOnlyList<string> UnusedHeaders,
    IReadOnlyList<RegisterTestRowDto> Rows,
    IReadOnlyList<string> UnmappedCodes,
    IReadOnlyList<string> Warnings,
    int Checks,
    decimal TotalGross,
    decimal TotalNet);

/// <summary>Compares a sample of the provider's hours import template (its header row) with an hours layout.</summary>
public sealed record TestHoursTemplateRequest(string Content, HoursLayoutDto? Layout = null);

public sealed record HoursTemplateColumnDto(string TemplateHeader, int Position, string? LayoutHeader, int? LayoutPosition, bool SamePosition);

/// <summary>
/// Each template header and the layout column that writes it (or none), layout columns the template doesn't have,
/// and an example of the export with the layout.
/// </summary>
public sealed record HoursTemplateTestDto(bool Ok, string? Error, IReadOnlyList<HoursTemplateColumnDto> Columns, IReadOnlyList<string> ExtraLayoutColumns, string Example);

public interface IPayrollLayoutService
{
    /// <summary>The provider's layouts (the built-in default for any part not configured).</summary>
    Task<PayrollLayoutDto> GetAsync(string providerKey, CancellationToken cancellationToken = default);

    Task<PayrollLayoutDto> GetDefaultAsync(string providerKey, CancellationToken cancellationToken = default);

    /// <summary>Saves both layouts. Audited.</summary>
    Task<PayrollLayoutDto> SaveAsync(string providerKey, SavePayrollLayoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Goes back to the built-in layouts. Audited.</summary>
    Task<PayrollLayoutDto> ResetAsync(string providerKey, CancellationToken cancellationToken = default);

    Task<RegisterTestResultDto> TestRegisterAsync(string providerKey, TestRegisterSampleRequest request, CancellationToken cancellationToken = default);

    Task<HoursTemplateTestDto> TestHoursTemplateAsync(string providerKey, TestHoursTemplateRequest request, CancellationToken cancellationToken = default);
}
